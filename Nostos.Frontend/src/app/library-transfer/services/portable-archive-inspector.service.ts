/**
 * Bounded browser-side inspection of a selected archive (plan §10).
 *
 * This is not a security validator — the server repeats full #678 validation
 * after upload. It exists to identify obvious wrong files early, derive the
 * preflight request, and never read the whole archive:
 *
 * 1. read only the ZIP tail needed to find the EOCD (and ZIP64 locator);
 * 2. read the bounded central directory;
 * 3. locate `manifest.json` and read only that entry (bounded, inflating if
 *    the producer stored it deflated);
 * 4. parse bounded JSON and derive the summary/preflight request.
 */

import { Injectable } from '@angular/core';

import {
  ArchiveInspection,
  ArchiveInspectionReason,
  PortableArchiveSummary,
  TransferCancelledError,
} from '../models/library-transfer.models';
import {
  MigrationArchiveCountsDto,
  MigrationPreflightRequestDto,
  MIGRATION_LIMITS,
} from '../models/migration-http.dtos';
import { readBlobRange } from './hash/blob-bytes';

const EOCD_SIGNATURE = 0x06054b50;
const ZIP64_EOCD_LOCATOR_SIGNATURE = 0x07064b50;
const ZIP64_EOCD_SIGNATURE = 0x06064b50;
const CENTRAL_DIRECTORY_SIGNATURE = 0x02014b50;
const LOCAL_FILE_HEADER_SIGNATURE = 0x04034b50;
const ZIP64_EXTRA_FIELD = 0x0001;

const PORTABLE_FORMAT_NAME = 'nostos-portable';
const BACKUP_FORMAT_NAMES = new Set(['nostos-operational-backup', 'operational-backup']);

const MAX_MANIFEST_BYTES = Number(MIGRATION_LIMITS.maxManifestBytes);
const MAX_MANIFEST_COMPRESSED_BYTES = 2 * MAX_MANIFEST_BYTES;
const MAX_CENTRAL_DIRECTORY_BYTES = 16 * 1024 * 1024;
const EOCD_SCAN_BYTES = 22 + 0xffff;

export interface InspectArchiveOptions {
  signal?: AbortSignal;
  /** Total bytes read across every slice, for progress and bounds assertions. */
  onProgress?: (bytesRead: number, totalBytes: number) => void;
}

interface CentralDirectoryEntry {
  name: string;
  method: number;
  compressedSize: number;
  uncompressedSize: number;
  localHeaderOffset: number;
}

interface ZipDirectory {
  entries: CentralDirectoryEntry[];
}

class InspectionFailure extends Error {
  constructor(
    readonly reason: ArchiveInspectionReason,
    message: string,
  ) {
    super(message);
    this.name = 'InspectionFailure';
  }
}

@Injectable({ providedIn: 'root' })
export class PortableArchiveInspector {
  async inspect(file: Blob, options: InspectArchiveOptions = {}): Promise<ArchiveInspection> {
    let bytesRead = 0;
    const read = async (offset: number, length: number): Promise<Uint8Array> => {
      try {
        this.throwIfAborted(options.signal);
        const bytes = await readBlobRange(file, offset, length);
        bytesRead += bytes.length;
        options.onProgress?.(bytesRead, file.size);
        return bytes;
      } catch (error) {
        if (error instanceof RangeError) {
          throw new InspectionFailure('truncated', 'Archive ended before a declared structure.');
        }
        throw error;
      }
    };

    try {
      const directory = await this.readDirectory(file, read);
      const manifestEntry = this.findManifest(directory.entries);
      const manifestBytes = await this.readManifestEntry(file, manifestEntry, read);
      return this.interpretManifest(manifestBytes, file.size);
    } catch (error) {
      if (error instanceof TransferCancelledError) throw error;
      if (error instanceof InspectionFailure) {
        return { kind: 'unsupported', reason: error.reason };
      }
      return { kind: 'unsupported', reason: 'not-a-zip' };
    }
  }

  private throwIfAborted(signal?: AbortSignal): void {
    if (signal?.aborted) throw new TransferCancelledError('Archive inspection was cancelled.');
  }

  private async readDirectory(
    file: Blob,
    read: (offset: number, length: number) => Promise<Uint8Array>,
  ): Promise<ZipDirectory> {
    if (file.size < 22) {
      throw new InspectionFailure('not-a-zip', 'File is too small to be a ZIP archive.');
    }

    const tailLength = Math.min(file.size, EOCD_SCAN_BYTES);
    const tailStart = file.size - tailLength;
    const tail = await read(tailStart, tailLength);
    const eocdOffset = this.findEocd(tail, tailStart);
    if (eocdOffset < 0) {
      throw new InspectionFailure('not-a-zip', 'No ZIP end-of-central-directory record found.');
    }

    const eocdView = new DataView(tail.buffer, tail.byteOffset, tail.byteLength);
    const eocdLocal = eocdOffset - tailStart;
    let totalEntries = eocdView.getUint16(eocdLocal + 10, true);
    let centralDirectorySize = eocdView.getUint32(eocdLocal + 12, true);
    let centralDirectoryOffset = eocdView.getUint32(eocdLocal + 16, true);

    if (
      totalEntries === 0xffff ||
      centralDirectorySize === 0xffffffff ||
      centralDirectoryOffset === 0xffffffff ||
      (await this.hasZip64Locator(eocdOffset, read))
    ) {
      const zip64 = await this.readZip64Eocd(eocdOffset, read);
      totalEntries = zip64.totalEntries;
      centralDirectorySize = zip64.centralDirectorySize;
      centralDirectoryOffset = zip64.centralDirectoryOffset;
    }

    if (totalEntries > MIGRATION_LIMITS.maxArchiveEntries) {
      throw new InspectionFailure(
        'too-many-entries',
        `Archive declares ${totalEntries} entries, above the contract limit.`,
      );
    }
    if (centralDirectorySize > MAX_CENTRAL_DIRECTORY_BYTES) {
      throw new InspectionFailure(
        'too-many-entries',
        'Archive central directory exceeds the bounded inspection limit.',
      );
    }
    if (centralDirectoryOffset + centralDirectorySize > file.size) {
      throw new InspectionFailure('truncated', 'Central directory reaches past end of file.');
    }

    const directoryBytes = await read(centralDirectoryOffset, centralDirectorySize);
    const view = new DataView(
      directoryBytes.buffer,
      directoryBytes.byteOffset,
      directoryBytes.byteLength,
    );
    const entries: CentralDirectoryEntry[] = [];
    let pointer = 0;

    for (let index = 0; index < totalEntries; index += 1) {
      if (pointer + 46 > directoryBytes.length) {
        throw new InspectionFailure('truncated', 'Central directory ended mid-entry.');
      }
      if (view.getUint32(pointer, true) !== CENTRAL_DIRECTORY_SIGNATURE) {
        throw new InspectionFailure('not-a-zip', 'Central directory entry signature is invalid.');
      }

      const method = view.getUint16(pointer + 10, true);
      let compressedSize = view.getUint32(pointer + 20, true);
      let uncompressedSize = view.getUint32(pointer + 24, true);
      const nameLength = view.getUint16(pointer + 28, true);
      const extraLength = view.getUint16(pointer + 30, true);
      const commentLength = view.getUint16(pointer + 32, true);
      let localHeaderOffset = view.getUint32(pointer + 42, true);
      const nameStart = pointer + 46;
      const nameEnd = nameStart + nameLength;
      const extraEnd = nameEnd + extraLength;

      if (extraEnd > directoryBytes.length) {
        throw new InspectionFailure('truncated', 'Central directory entry name/extra overrun.');
      }

      const hasZip64 =
        compressedSize === 0xffffffff ||
        uncompressedSize === 0xffffffff ||
        localHeaderOffset === 0xffffffff;
      if (hasZip64) {
        const zip64 = this.readZip64Extra(directoryBytes.subarray(nameEnd, extraEnd), {
          compressedSize,
          uncompressedSize,
          localHeaderOffset,
        });
        compressedSize = zip64.compressedSize;
        uncompressedSize = zip64.uncompressedSize;
        localHeaderOffset = zip64.localHeaderOffset;
      }

      entries.push({
        name: new TextDecoder().decode(directoryBytes.subarray(nameStart, nameEnd)),
        method,
        compressedSize,
        uncompressedSize,
        localHeaderOffset,
      });

      pointer = extraEnd + commentLength;
    }

    return { entries };
  }

  private findEocd(tail: Uint8Array, tailStart: number): number {
    if (tail.length < 22) return -1;
    const view = new DataView(tail.buffer, tail.byteOffset, tail.byteLength);
    for (let index = tail.length - 22; index >= 0; index -= 1) {
      if (view.getUint32(index, true) !== EOCD_SIGNATURE) continue;
      const commentLength = view.getUint16(index + 20, true);
      if (index + 22 + commentLength <= tail.length) return tailStart + index;
    }
    return -1;
  }

  private async hasZip64Locator(
    eocdOffset: number,
    read: (offset: number, length: number) => Promise<Uint8Array>,
  ): Promise<boolean> {
    if (eocdOffset < 20) return false;
    const locator = await read(eocdOffset - 20, 20);
    const view = new DataView(locator.buffer, locator.byteOffset, locator.byteLength);
    return view.getUint32(0, true) === ZIP64_EOCD_LOCATOR_SIGNATURE;
  }

  private async readZip64Eocd(
    eocdOffset: number,
    read: (offset: number, length: number) => Promise<Uint8Array>,
  ): Promise<{ totalEntries: number; centralDirectorySize: number; centralDirectoryOffset: number }> {
    if (eocdOffset < 20) {
      throw new InspectionFailure('truncated', 'ZIP64 locator is missing before the EOCD.');
    }
    const locator = await read(eocdOffset - 20, 20);
    const locatorView = new DataView(locator.buffer, locator.byteOffset, locator.byteLength);
    if (locatorView.getUint32(0, true) !== ZIP64_EOCD_LOCATOR_SIGNATURE) {
      throw new InspectionFailure('truncated', 'ZIP64 locator signature is invalid.');
    }

    const recordOffset = Number(locatorView.getBigUint64(8, true));
    const record = await read(recordOffset, 56);
    const view = new DataView(record.buffer, record.byteOffset, record.byteLength);
    if (view.getUint32(0, true) !== ZIP64_EOCD_SIGNATURE) {
      throw new InspectionFailure('truncated', 'ZIP64 end-of-central-directory is invalid.');
    }

    return {
      totalEntries: Number(view.getBigUint64(32, true)),
      centralDirectorySize: Number(view.getBigUint64(40, true)),
      centralDirectoryOffset: Number(view.getBigUint64(48, true)),
    };
  }

  private readZip64Extra(
    extra: Uint8Array,
    fallback: { compressedSize: number; uncompressedSize: number; localHeaderOffset: number },
  ): { compressedSize: number; uncompressedSize: number; localHeaderOffset: number } {
    const view = new DataView(extra.buffer, extra.byteOffset, extra.byteLength);
    let pointer = 0;
    while (pointer + 4 <= extra.length) {
      const headerId = view.getUint16(pointer, true);
      const dataSize = view.getUint16(pointer + 2, true);
      if (pointer + 4 + dataSize > extra.length) break;
      if (headerId === ZIP64_EXTRA_FIELD) {
        let cursor = pointer + 4;
        const result = { ...fallback };
        if (fallback.uncompressedSize === 0xffffffff && cursor + 8 <= pointer + 4 + dataSize) {
          result.uncompressedSize = Number(view.getBigUint64(cursor, true));
          cursor += 8;
        }
        if (fallback.compressedSize === 0xffffffff && cursor + 8 <= pointer + 4 + dataSize) {
          result.compressedSize = Number(view.getBigUint64(cursor, true));
          cursor += 8;
        }
        if (fallback.localHeaderOffset === 0xffffffff && cursor + 8 <= pointer + 4 + dataSize) {
          result.localHeaderOffset = Number(view.getBigUint64(cursor, true));
        }
        return result;
      }
      pointer += 4 + dataSize;
    }
    return fallback;
  }

  private findManifest(entries: readonly CentralDirectoryEntry[]): CentralDirectoryEntry {
    const manifests = entries.filter((entry) => entry.name.toLowerCase() === 'manifest.json');
    if (manifests.length === 0) {
      throw new InspectionFailure('missing-manifest', 'Archive has no manifest.json entry.');
    }
    if (manifests.length > 1) {
      throw new InspectionFailure('duplicate-manifest', 'Archive has duplicate manifest.json entries.');
    }
    return manifests[0];
  }

  private async readManifestEntry(
    file: Blob,
    entry: CentralDirectoryEntry,
    read: (offset: number, length: number) => Promise<Uint8Array>,
  ): Promise<Uint8Array> {
    if (
      entry.uncompressedSize > MAX_MANIFEST_BYTES ||
      entry.compressedSize > MAX_MANIFEST_COMPRESSED_BYTES
    ) {
      throw new InspectionFailure('manifest-too-large', 'manifest.json exceeds the 4 MiB limit.');
    }

    const header = await read(entry.localHeaderOffset, 30);
    const view = new DataView(header.buffer, header.byteOffset, header.byteLength);
    if (view.getUint32(0, true) !== LOCAL_FILE_HEADER_SIGNATURE) {
      throw new InspectionFailure('not-a-zip', 'Local file header signature is invalid.');
    }

    const nameLength = view.getUint16(26, true);
    const extraLength = view.getUint16(28, true);
    const dataStart = entry.localHeaderOffset + 30 + nameLength + extraLength;
    if (dataStart + entry.compressedSize > file.size) {
      throw new InspectionFailure('truncated', 'manifest.json bytes reach past end of file.');
    }

    const compressed = await read(dataStart, entry.compressedSize);
    if (entry.method === 0) return compressed;
    if (entry.method === 8) return inflateRaw(compressed);
    throw new InspectionFailure(
      'unsupported-compression-method',
      `manifest.json uses compression method ${entry.method}.`,
    );
  }

  private interpretManifest(bytes: Uint8Array, archiveBytes: number): ArchiveInspection {
    let parsed: unknown;
    try {
      parsed = JSON.parse(new TextDecoder().decode(bytes));
    } catch {
      throw new InspectionFailure('malformed-manifest', 'manifest.json is not valid JSON.');
    }

    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      throw new InspectionFailure('malformed-manifest', 'manifest.json is not a JSON object.');
    }

    const manifest = parsed as Record<string, unknown>;
    const format = typeof manifest['format'] === 'string' ? manifest['format'] : '';
    if (BACKUP_FORMAT_NAMES.has(format.toLowerCase())) return { kind: 'operational-backup' };

    if (format !== PORTABLE_FORMAT_NAME) {
      if (looksLikeOperationalBackupManifest(manifest)) return { kind: 'operational-backup' };
      throw new InspectionFailure('malformed-manifest', 'manifest.json does not declare a format.');
    }

    const formatVersion = requireInteger(manifest['formatVersion']);
    const dataVersion = requireInteger(manifest['dataVersion']);
    if (formatVersion !== 1) {
      throw new InspectionFailure(
        'unsupported-format-version',
        `Archive format version ${manifest['formatVersion']} is not supported.`,
      );
    }
    if (dataVersion !== 1 && dataVersion !== 2 && dataVersion !== 3) {
      throw new InspectionFailure(
        'unsupported-data-version',
        `Archive data version ${manifest['dataVersion']} is not supported.`,
      );
    }

    // Bytes are counted by the caller's read helper; the summary carries the
    // declared sizes so the preflight request is constructed without re-reading.
    return {
      kind: 'portable',
      summary: buildSummary(manifest, formatVersion, dataVersion, archiveBytes),
    };
  }
}

function looksLikeOperationalBackupManifest(manifest: Record<string, unknown>): boolean {
  return (
    typeof manifest['databaseSizeBytes'] === 'number' &&
    typeof manifest['timestamp'] === 'string' &&
    typeof manifest['checksum'] === 'string'
  );
}

function requireInteger(value: unknown): number {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new InspectionFailure('malformed-manifest', 'manifest.json version fields are invalid.');
  }
  return value;
}

function buildSummary(
  manifest: Record<string, unknown>,
  formatVersion: number,
  dataVersion: number,
  archiveBytes: number,
): PortableArchiveSummary {
  const countsSource =
    typeof manifest['counts'] === 'object' && manifest['counts'] !== null
      ? (manifest['counts'] as Record<string, unknown>)
      : {};
  const media = Array.isArray(manifest['media']) ? manifest['media'] : [];
  const mediaLengths = media.map((item) => {
    if (typeof item === 'object' && item !== null) {
      const length = (item as Record<string, unknown>)['length'];
      if (typeof length === 'number' && length >= 0) return length;
    }
    return 0;
  });
  const dataLength =
    typeof manifest['data'] === 'object' && manifest['data'] !== null
      ? nonNegativeNumber((manifest['data'] as Record<string, unknown>)['length'])
      : 0;

  const counts = mapCounts(countsSource, media.length);
  return {
    formatName: PORTABLE_FORMAT_NAME,
    formatVersion,
    dataVersion,
    archiveBytes,
    mediaBytes: mediaLengths.reduce((total, length) => total + length, 0),
    maxEntryBytes: Math.max(dataLength, ...mediaLengths, 0),
    counts,
    mediaEntries: media.length,
  };
}

function mapCounts(source: Record<string, unknown>, mediaEntries: number): MigrationArchiveCountsDto {
  const value = (key: string): number => nonNegativeNumber(source[key]);
  const counts: MigrationArchiveCountsDto = {
    works: value('works'),
    books: value('books'),
    notes: value('notes'),
    topics: value('topics'),
    noteTopics: value('noteTopics'),
    writings: value('writings'),
    // The v1 manifest counts predate these data versions; the server derives
    // the authoritative values from the relational payload after upload.
    writingNotes: value('writingNotes'),
    collections: value('collections'),
    collectionMemberships: value('bookCollections'),
    acquisitions: value('bookAcquisitions'),
    assistantSettings: value('assistantSettings'),
    noteImportBookLinks: value('noteImportBookLinks'),
    mediaEntries,
    totalRows: 0,
  };
  counts.totalRows =
    counts.works +
    counts.books +
    counts.notes +
    counts.topics +
    counts.noteTopics +
    counts.writings +
    counts.writingNotes +
    counts.collections +
    counts.collectionMemberships +
    counts.acquisitions +
    counts.assistantSettings +
    counts.noteImportBookLinks;
  return counts;
}

function nonNegativeNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0;
}

async function inflateRaw(compressed: Uint8Array): Promise<Uint8Array> {
  const Decompression = (globalThis as { DecompressionStream?: typeof DecompressionStream })
    .DecompressionStream;
  if (!Decompression) throw new InspectionFailure('manifest-unreadable', 'No inflate support.');

  const stream = new Decompression('deflate-raw');
  const writer = stream.writable.getWriter();
  void writer.write(compressed as unknown as BufferSource);
  void writer.close();

  const reader = stream.readable.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    chunks.push(value);
    total += value.length;
    if (total > MAX_MANIFEST_BYTES) {
      throw new InspectionFailure('manifest-too-large', 'Inflated manifest exceeds 4 MiB.');
    }
  }

  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

/** Builds the preflight request from a summary and the selected file. */
export function preflightRequestFromSummary(
  file: Blob,
  summary: PortableArchiveSummary,
  clientDestinationRevision: string | null = null,
): MigrationPreflightRequestDto {
  return {
    incomingCounts: summary.counts,
    declaredArchiveBytes: file.size,
    declaredMediaBytes: summary.mediaBytes,
    maxSingleEntryBytes: summary.maxEntryBytes,
    declaredFormatVersion: summary.formatVersion,
    declaredDataVersion: summary.dataVersion,
    declaredFormatName: summary.formatName,
    clientDestinationRevision,
    isOperationalBackup: false,
  };
}
