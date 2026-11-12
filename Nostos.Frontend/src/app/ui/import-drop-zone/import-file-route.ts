/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

/**
 * Which import flow a dropped file belongs to (issue #1526).
 *
 * The server decides a highlight source by content, and a reading history by
 * its header row. The drop zone only needs the flow, so it reads the
 * extension and, for a CSV, the header row — the same columns the server's
 * readers check. Anything it cannot tell is sent to the server, which stays
 * the authority: that is the fallback, not the first answer.
 */

export type ImportFileKind = 'highlights' | 'reading-history' | 'archive' | 'calibre' | 'unknown';

export type ImportFlow = 'highlights' | 'reading-history' | 'archive' | 'calibre';

/** What the client could not settle, and the server's answer if it was asked. */
export interface ImportProbe {
  flow: 'highlights' | 'reading-history' | null;
  /** The last error the server gave, when neither import accepted the file. */
  message: string | null;
}

export type ImportProbeFile = (file: File) => Promise<ImportProbe>;

export type ImportRoute =
  | { kind: 'highlights'; files: File[] }
  | { kind: 'reading-history'; file: File }
  | { kind: 'archive'; file: File }
  | { kind: 'calibre'; file: File }
  | { kind: 'unknown'; message: string; serverMessage: string | null };

/** What the host offers beyond the file types every host reads. */
export interface ImportRouteOptions {
  /**
   * The server accepts a zipped Calibre library through the resumable upload
   * (#1572). Without it a `.zip` is like any other unrecognised file.
   */
  readonly calibreZip?: boolean;
}

const HIGHLIGHT_EXTENSIONS = ['.sqlite', '.sqlite3', '.db', '.lua', '.txt'];
const ARCHIVE_EXTENSION = '.nostos';
const ZIP_EXTENSION = '.zip';
/** A CSV header is a single line; a big file is not read past its first KB. */
const HEADER_BYTES = 8 * 1024;
/** The central directory sits at the end; 64 KB holds it for a Calibre library. */
const ZIP_TAIL_BYTES = 64 * 1024;

export function hasExtension(name: string, extension: string): boolean {
  return name.toLowerCase().endsWith(extension);
}

/** A `.nostos` file is a ZIP; the first four bytes say so. */
async function looksLikeZip(file: File): Promise<boolean> {
  try {
    const bytes = new Uint8Array(await file.slice(0, 4).arrayBuffer());
    return bytes[0] === 0x50 && bytes[1] === 0x4b;
  } catch {
    // An unreadable slice must not turn a real archive away; the import flow
    // validates the archive itself.
    return true;
  }
}

/** The first record of a CSV, with quoted fields kept whole. */
export function csvHeader(text: string): string[] {
  const line = text.replace(/^\uFEFF/, '').split(/\r?\n/, 1)[0] ?? '';
  const values: string[] = [];
  let value = '';
  let quoted = false;
  for (const character of line) {
    if (quoted) {
      if (character === '"') quoted = false;
      else value += character;
    } else if (character === '"') {
      quoted = true;
    } else if (character === ',') {
      values.push(value);
      value = '';
    } else {
      value += character;
    }
  }
  values.push(value);
  return values.map((entry) => entry.trim());
}

function columnIndex(header: readonly string[], name: string): number {
  return header.findIndex((column) => column.toLowerCase() === name.toLowerCase());
}

export function looksLikeGoodreads(header: readonly string[]): boolean {
  return columnIndex(header, 'Book Id') >= 0
    && columnIndex(header, 'Exclusive Shelf') >= 0
    && columnIndex(header, 'My Rating') >= 0;
}

export function looksLikeStorygraph(header: readonly string[]): boolean {
  return columnIndex(header, 'Read Status') >= 0
    && columnIndex(header, 'Star Rating') >= 0
    && columnIndex(header, 'ISBN/UID') >= 0;
}

export function looksLikeReadwise(header: readonly string[]): boolean {
  return columnIndex(header, 'Highlight') >= 0 && columnIndex(header, 'Book Title') >= 0;
}

/**
 * The entry names in a zip's central directory, without decompressing
 * anything — a Calibre library can be gigabytes. `null` means the directory
 * could not be read (an unsupported or damaged zip), which is not the same as
 * "no metadata.db".
 */
export async function zipEntryNames(file: File): Promise<string[] | null> {
  try {
    const tailStart = Math.max(0, file.size - ZIP_TAIL_BYTES);
    const tail = new Uint8Array(await file.slice(tailStart).arrayBuffer());
    const view = new DataView(tail.buffer, tail.byteOffset, tail.byteLength);
    const eocd = findEndOfCentralDirectory(view);
    if (eocd < 0) return null;

    let entries = view.getUint16(eocd + 10, true);
    let directorySize = view.getUint32(eocd + 12, true);
    let directoryOffset = view.getUint32(eocd + 16, true);

    if (entries === 0xffff || directoryOffset === 0xffffffff || directorySize === 0xffffffff) {
      // ZIP64: the counts and the directory's address are in the record the
      // locator points at, and that record can sit before the tail above.
      const locator = eocd - 20;
      if (locator < 0 || view.getUint32(locator, true) !== 0x07064b50) return null;
      const recordOffset = Number(view.getBigUint64(locator + 8, true));
      if (!Number.isSafeInteger(recordOffset)) return null;
      const record = new Uint8Array(
        await file.slice(recordOffset, recordOffset + 56).arrayBuffer(),
      );
      const recordView = new DataView(record.buffer, record.byteOffset, record.byteLength);
      if (record.byteLength < 56 || recordView.getUint32(0, true) !== 0x06064b50) return null;
      entries = Number(recordView.getBigUint64(32, true));
      directorySize = Number(recordView.getBigUint64(40, true));
      directoryOffset = Number(recordView.getBigUint64(48, true));
    }

    if (
      !Number.isSafeInteger(entries) ||
      !Number.isSafeInteger(directorySize) ||
      !Number.isSafeInteger(directoryOffset)
    ) {
      return null;
    }
    if (entries <= 0 || directorySize <= 0) return [];

    const directory = new Uint8Array(
      await file.slice(directoryOffset, directoryOffset + directorySize).arrayBuffer(),
    );
    const names: string[] = [];
    const bytes = new DataView(directory.buffer, directory.byteOffset, directory.byteLength);
    let offset = 0;
    for (let index = 0; index < entries; index++) {
      if (offset + 46 > bytes.byteLength || bytes.getUint32(offset, true) !== 0x02014b50) {
        return null;
      }
      const nameLength = bytes.getUint16(offset + 28, true);
      const extraLength = bytes.getUint16(offset + 30, true);
      const commentLength = bytes.getUint16(offset + 32, true);
      const nameStart = offset + 46;
      if (nameStart + nameLength > bytes.byteLength) return null;
      names.push(new TextDecoder().decode(directory.subarray(nameStart, nameStart + nameLength)));
      offset = nameStart + nameLength + extraLength + commentLength;
    }
    return names;
  } catch {
    return null;
  }
}

function findEndOfCentralDirectory(view: DataView): number {
  // The record is 22 bytes plus a comment of at most 64 KB.
  for (let offset = view.byteLength - 22; offset >= 0; offset--) {
    if (view.getUint32(offset, true) === 0x06054b50) return offset;
  }
  return -1;
}

/**
 * True when the zip holds a Calibre library: `metadata.db` at its root or in
 * one top-level folder, which is what the server's extractor accepts.
 */
export function looksLikeCalibreZip(entries: readonly string[]): boolean {
  return entries.some((entry) => {
    const parts = entry.split('/').filter(Boolean);
    return parts.length <= 2 && parts[parts.length - 1].toLowerCase() === 'metadata.db';
  });
}

/** Which flow a file's extension and first bytes point at, or `unknown`. */
export async function classifyImportFile(
  file: File,
  options?: ImportRouteOptions,
): Promise<ImportFileKind> {
  if (file.size === 0) return 'unknown';

  const name = file.name.toLowerCase();
  if (hasExtension(name, ARCHIVE_EXTENSION)) {
    return (await looksLikeZip(file)) ? 'archive' : 'unknown';
  }

  // A zip is only an import when the host takes a zipped Calibre library
  // (#1572), and only when it holds the library's own metadata.db.
  if (hasExtension(name, ZIP_EXTENSION)) {
    if (options?.calibreZip !== true) return 'unknown';
    const entries = await zipEntryNames(file);
    return entries && looksLikeCalibreZip(entries) ? 'calibre' : 'unknown';
  }

  if (HIGHLIGHT_EXTENSIONS.some((extension) => hasExtension(name, extension))) return 'highlights';

  // Apple Books' recent changes live in the `-wal`/`-shm` files beside its
  // databases; they travel with the main file (issue #1357).
  if (hasExtension(name, '.sqlite-wal') || hasExtension(name, '.sqlite-shm')) return 'highlights';

  if (hasExtension(name, '.csv')) {
    try {
      const header = csvHeader(await file.slice(0, HEADER_BYTES).text());
      if (looksLikeGoodreads(header) || looksLikeStorygraph(header)) return 'reading-history';
      if (looksLikeReadwise(header)) return 'highlights';
    } catch {
      // An unreadable text file goes to the server like any other unknown.
    }
  }

  return 'unknown';
}

function unknown(message: string, serverMessage: string | null = null): ImportRoute {
  return { kind: 'unknown', message, serverMessage };
}

/**
 * The flow for one drop. Several files are one highlight import only: it is
 * the one flow that reads a set (a Kobo database, or KOReader sidecars, or
 * Apple Books' two databases). A reading history, an archive and a Calibre zip
 * are one file each, so they are asked for one at a time rather than importing
 * a guess.
 */
export async function routeImportFiles(
  files: readonly File[],
  probe?: ImportProbeFile,
  options?: ImportRouteOptions,
): Promise<ImportRoute> {
  if (files.length === 0) return unknown('Choose a file to import.');
  if (files.some((file) => file.size === 0)) {
    return unknown('That file is empty. Choose the export again.');
  }

  const kinds = await Promise.all(files.map((file) => classifyImportFile(file, options)));
  let probeMessage: string | null = null;

  const resolved = await Promise.all(
    kinds.map(async (kind, index): Promise<ImportFileKind> => {
      if (kind !== 'unknown' || !probe) return kind;
      const answer = await probe(files[index]);
      if (answer.message) probeMessage = answer.message;
      return answer.flow ?? 'unknown';
    }),
  );

  if (files.length > 1) {
    if (resolved.every((kind) => kind === 'highlights')) return { kind: 'highlights', files: [...files] };
    if (resolved.includes('archive')) {
      return unknown('Nostos imports one archive at a time. Drop the .nostos file on its own.');
    }
    if (resolved.includes('calibre')) {
      return unknown('Nostos imports one Calibre library at a time. Drop the zip on its own.');
    }
    if (resolved.includes('reading-history')) {
      return unknown('Nostos imports one reading history at a time. Drop the CSV on its own.');
    }
    return unknown('Nostos could not tell what these files hold.', probeMessage);
  }

  const file = files[0];
  switch (resolved[0]) {
    case 'highlights':
      return { kind: 'highlights', files: [file] };
    case 'reading-history':
      return { kind: 'reading-history', file };
    case 'archive':
      return { kind: 'archive', file };
    case 'calibre':
      return { kind: 'calibre', file };
    default:
      return unknown(
        hasExtension(file.name, ARCHIVE_EXTENSION)
          ? 'That file is not a Nostos archive. Choose a .nostos file exported from another Nostos.'
          : hasExtension(file.name, ZIP_EXTENSION) && options?.calibreZip === true
            ? 'That zip is not a Calibre library. A Calibre zip holds metadata.db at its root or in one top-level folder.'
            : 'Nostos could not read this file.',
        probeMessage,
      );
  }
}
