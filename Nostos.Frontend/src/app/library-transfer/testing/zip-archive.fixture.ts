/**
 * ZIP fixture builder for library-transfer specs.
 *
 * Builds real store/deflate archives (with optional ZIP64 records and
 * deliberately-lying declared sizes) so the inspector and hash code are
 * exercised against byte layouts rather than mocks of the parser.
 */

export interface ZipFixtureEntry {
  name: string;
  data: Uint8Array;
  /** 0 = store, 8 = deflate (default 0). */
  method?: 0 | 8;
  /** Only written to the central directory, to test size caps without data. */
  declaredUncompressedSize?: number;
  declaredCompressedSize?: number;
}

export interface ZipFixtureOptions {
  comment?: string;
  forceZip64?: boolean;
}

const LOCAL_FILE_HEADER = 0x04034b50;
const CENTRAL_DIRECTORY = 0x02014b50;
const EOCD = 0x06054b50;
const ZIP64_EOCD = 0x06064b50;
const ZIP64_LOCATOR = 0x07064b50;
const ZIP64_EXTRA = 0x0001;

export async function zipEntryData(entry: ZipFixtureEntry): Promise<Uint8Array> {
  if ((entry.method ?? 0) === 8) return deflateRaw(entry.data);
  return entry.data;
}

export async function buildZipArchive(
  entries: readonly ZipFixtureEntry[],
  options: ZipFixtureOptions = {},
): Promise<Uint8Array> {
  const encoder = new TextEncoder();
  const localChunks: Uint8Array[] = [];
  const centralChunks: Uint8Array[] = [];
  let offset = 0;

  for (const entry of entries) {
    const nameBytes = encoder.encode(entry.name);
    const payload = await zipEntryData(entry);
    const declaredCompressed = entry.declaredCompressedSize ?? payload.length;
    const declaredUncompressed = entry.declaredUncompressedSize ?? entry.data.length;
    const needsZip64 = options.forceZip64 === true;

    const localHeader = new Uint8Array(30 + nameBytes.length);
    const localView = new DataView(localHeader.buffer);
    localView.setUint32(0, LOCAL_FILE_HEADER, true);
    localView.setUint16(4, needsZip64 ? 45 : 20, true);
    localView.setUint16(6, 0, true);
    localView.setUint16(8, entry.method ?? 0, true);
    localView.setUint32(14, crc32(entry.data), true);
    localView.setUint32(18, needsZip64 ? 0xffffffff : declaredCompressed, true);
    localView.setUint32(22, needsZip64 ? 0xffffffff : declaredUncompressed, true);
    localView.setUint16(26, nameBytes.length, true);
    localView.setUint16(28, 0, true);
    localHeader.set(nameBytes, 30);

    localChunks.push(localHeader, payload);

    const zip64Extra =
      needsZip64 && declaredUncompressed < 0xffffffff && declaredCompressed < 0xffffffff
        ? buildZip64Extra(declaredUncompressed, declaredCompressed, offset)
        : new Uint8Array(0);
    const centralHeader = new Uint8Array(46 + nameBytes.length + zip64Extra.length);
    const centralView = new DataView(centralHeader.buffer);
    centralView.setUint32(0, CENTRAL_DIRECTORY, true);
    centralView.setUint16(4, 45, true);
    centralView.setUint16(6, 20, true);
    centralView.setUint16(8, 0, true);
    centralView.setUint16(10, entry.method ?? 0, true);
    centralView.setUint32(16, crc32(entry.data), true);
    centralView.setUint32(
      20,
      needsZip64 ? 0xffffffff : declaredCompressed,
      true,
    );
    centralView.setUint32(
      24,
      needsZip64 ? 0xffffffff : declaredUncompressed,
      true,
    );
    centralView.setUint16(28, nameBytes.length, true);
    centralView.setUint16(30, zip64Extra.length, true);
    centralView.setUint32(42, needsZip64 ? 0xffffffff : offset, true);
    centralHeader.set(nameBytes, 46);
    centralHeader.set(zip64Extra, 46 + nameBytes.length);

    centralChunks.push(centralHeader);
    offset += localHeader.length + payload.length;
  }

  const centralDirectoryOffset = offset;
  const centralDirectorySize = centralChunks.reduce((total, chunk) => total + chunk.length, 0);
  const centralDirectory = concat(centralChunks);
  const comment = encoder.encode(options.comment ?? '');

  const trailer: Uint8Array[] = [...localChunks, centralDirectory];
  const eocdLength = 22 + comment.length;
  const zip64Needed =
    options.forceZip64 === true ||
    centralDirectoryOffset > 0xffffffff ||
    centralDirectorySize > 0xffffffff ||
    entries.length > 0xffff;

  if (zip64Needed) {
    const eocd64 = new Uint8Array(56);
    const eocd64View = new DataView(eocd64.buffer);
    eocd64View.setUint32(0, ZIP64_EOCD, true);
    eocd64View.setBigUint64(4, 44n, true);
    eocd64View.setUint16(12, 45, true);
    eocd64View.setUint16(14, 45, true);
    eocd64View.setBigUint64(24, BigInt(entries.length), true);
    eocd64View.setBigUint64(32, BigInt(entries.length), true);
    eocd64View.setBigUint64(40, BigInt(centralDirectorySize), true);
    eocd64View.setBigUint64(48, BigInt(centralDirectoryOffset), true);

    const locator = new Uint8Array(20);
    const locatorView = new DataView(locator.buffer);
    locatorView.setUint32(0, ZIP64_LOCATOR, true);
    locatorView.setBigUint64(8, BigInt(centralDirectoryOffset + centralDirectorySize), true);
    locatorView.setUint32(16, 1, true);

    trailer.push(eocd64, locator);
  }

  const eocd = new Uint8Array(eocdLength);
  const eocdView = new DataView(eocd.buffer);
  eocdView.setUint32(0, EOCD, true);
  eocdView.setUint16(8, Math.min(entries.length, 0xffff), true);
  eocdView.setUint16(10, Math.min(entries.length, 0xffff), true);
  eocdView.setUint32(12, Math.min(centralDirectorySize, 0xffffffff), true);
  eocdView.setUint32(16, Math.min(centralDirectoryOffset, 0xffffffff), true);
  eocdView.setUint16(20, comment.length, true);
  eocd.set(comment, 22);
  trailer.push(eocd);

  return concat(trailer);
}

function buildZip64Extra(
  uncompressedSize: number,
  compressedSize: number,
  localHeaderOffset: number,
): Uint8Array {
  const extra = new Uint8Array(4 + 24);
  const view = new DataView(extra.buffer);
  view.setUint16(0, ZIP64_EXTRA, true);
  view.setUint16(2, 24, true);
  view.setBigUint64(4, BigInt(uncompressedSize), true);
  view.setBigUint64(12, BigInt(compressedSize), true);
  view.setBigUint64(20, BigInt(localHeaderOffset), true);
  return extra;
}

export function concat(chunks: readonly Uint8Array[]): Uint8Array {
  const result = new Uint8Array(chunks.reduce((total, chunk) => total + chunk.length, 0));
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

export function createFile(bytes: Uint8Array, name = 'library.nostos'): File {
  return new File([bytes as unknown as BlobPart], name, {
    type: 'application/vnd.nostos.portable+zip',
  });
}

export function portableManifest(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    format: 'nostos-portable',
    formatVersion: 1,
    dataVersion: 3,
    exportedAtUtc: '2026-10-01T00:00:00Z',
    applicationVersion: '1.0.0',
    counts: {
      works: 2,
      books: 3,
      collections: 1,
      bookCollections: 4,
      notes: 5,
      topics: 1,
      noteTopics: 2,
      writings: 1,
      bookAcquisitions: 3,
    },
    data: { path: 'data/library.json', length: 1200, sha256: 'a'.repeat(64) },
    media: [],
    ...overrides,
  };
}

export function backupManifest(): Record<string, unknown> {
  return {
    version: '1',
    timestamp: '2026-10-01T00:00:00Z',
    databaseSizeBytes: 4096,
    bookFileCount: 3,
    totalSizeBytes: 8192,
    checksum: 'b'.repeat(64),
  };
}

export async function portableArchiveFixture(
  manifest: Record<string, unknown> = portableManifest(),
  options: { compressManifest?: boolean; extraEntries?: ZipFixtureEntry[] } = {},
): Promise<Uint8Array> {
  return buildZipArchive([
    {
      name: 'manifest.json',
      data: new TextEncoder().encode(JSON.stringify(manifest)),
      method: options.compressManifest ? 8 : 0,
    },
    ...(options.extraEntries ?? []),
  ]);
}

export async function deflateRaw(data: Uint8Array): Promise<Uint8Array> {
  const stream = new CompressionStream('deflate-raw');
  const writer = stream.writable.getWriter();
  void writer.write(data as unknown as BufferSource);
  void writer.close();

  const reader = stream.readable.getReader();
  const chunks: Uint8Array[] = [];
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    chunks.push(value);
  }
  return concat(chunks);
}

let crcTable: Uint32Array | null = null;

export function crc32(bytes: Uint8Array): number {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let index = 0; index < 256; index += 1) {
      let value = index;
      for (let bit = 0; bit < 8; bit += 1) {
        value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
      }
      crcTable[index] = value >>> 0;
    }
  }

  let crc = 0xffffffff;
  for (const byte of bytes) crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

/**
 * Blob double with a declared size and only small readable windows. Used to
 * prove inspection never reads an archive-sized range.
 */
export class SparseBlobDouble {
  readonly size: number;
  readonly requestedRanges: Array<{ start: number; end: number }> = [];
  readonly readBytes: number[] = [];

  constructor(
    size: number,
    private readonly windows: Array<{ start: number; end: number; bytes: Uint8Array }>,
  ) {
    this.size = size;
  }

  slice(start: number, end: number): Blob {
    const clampedEnd = Math.min(end, this.size);
    this.requestedRanges.push({ start, end: clampedEnd });
    const length = clampedEnd - start;
    const bytes = new Uint8Array(length);
    for (const window of this.windows) {
      const from = Math.max(start, window.start);
      const to = Math.min(clampedEnd, window.end);
      if (from >= to) continue;
      bytes.set(
        window.bytes.subarray(from - window.start, to - window.start),
        from - start,
      );
    }
    this.readBytes.push(length);
    return {
      size: length,
      arrayBuffer: async () => bytes.buffer,
    } as unknown as Blob;
  }
}
