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

export type ImportFileKind = 'highlights' | 'reading-history' | 'archive' | 'unknown';

export type ImportFlow = 'highlights' | 'reading-history' | 'archive';

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
  | { kind: 'unknown'; message: string; serverMessage: string | null };

const HIGHLIGHT_EXTENSIONS = ['.sqlite', '.sqlite3', '.db', '.lua', '.txt'];
const ARCHIVE_EXTENSION = '.nostos';
/** A CSV header is a single line; a big file is not read past its first KB. */
const HEADER_BYTES = 8 * 1024;

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

/** Which flow a file's extension and first bytes point at, or `unknown`. */
export async function classifyImportFile(file: File): Promise<ImportFileKind> {
  if (file.size === 0) return 'unknown';

  const name = file.name.toLowerCase();
  if (hasExtension(name, ARCHIVE_EXTENSION)) {
    return (await looksLikeZip(file)) ? 'archive' : 'unknown';
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
 * Apple Books' two databases). A reading history and an archive are one file
 * each, so they are asked for one at a time rather than importing a guess.
 */
export async function routeImportFiles(
  files: readonly File[],
  probe?: ImportProbeFile,
): Promise<ImportRoute> {
  if (files.length === 0) return unknown('Choose a file to import.');
  if (files.some((file) => file.size === 0)) {
    return unknown('That file is empty. Choose the export again.');
  }

  const kinds = await Promise.all(files.map((file) => classifyImportFile(file)));
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
    default:
      return unknown(
        hasExtension(file.name, ARCHIVE_EXTENSION)
          ? 'That file is not a Nostos archive. Choose a .nostos file exported from another Nostos.'
          : 'Nostos could not read this file.',
        probeMessage,
      );
  }
}
