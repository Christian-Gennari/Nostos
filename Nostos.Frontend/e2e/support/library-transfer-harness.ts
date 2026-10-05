/**
 * Library-transfer (#680 slice B10) browser-QA harness.
 *
 * Reads the multi-instance fixture state written by
 * `library-transfer-fixture.mjs`, exposes API/REST helpers for assertions and
 * for the controlled library changes the replacement scenarios need, and
 * implements the assignment's "validate the archive with the backend's own
 * reader/verifier" step by driving the real migration HTTP API from Node.
 */
import { execFileSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import path from 'node:path';

export interface LibraryTransferInstance {
  name: string;
  seed: 'a' | 'b' | 'none';
  tempDir: string;
  port: number;
  baseUrl: string;
  backendPid: number | null;
  startedAt: string;
  seeded: null | {
    variant: string;
    bookIds: Record<string, string>;
    noteIds: string[];
    collectionIds: Record<string, string>;
  };
}

export interface LibraryFacts {
  books: number;
  notes: number;
  highlights: number;
  collections: number;
  collectionMemberships: number;
  progress: { book: string; percentage: number };
  writingName?: string;
  writingContent?: string;
}

export interface LibraryTransferFixtureState {
  instances: Record<string, LibraryTransferInstance>;
  chunkSizeBytes: number;
  builtAt?: string;
  libraries?: Record<string, LibraryFacts>;
}

const HERE = path.dirname(__filename);
export const TRANSFER_STATE_FILE = path.join(HERE, 'library-transfer-fixture-state.json');
export const TRANSFER_LAUNCHER = path.join(HERE, 'library-transfer-fixture.mjs');
export const TRANSFER_ARTIFACTS = path.join(HERE, '..', 'test-results', 'library-transfer');

export function loadTransferFixture(): LibraryTransferFixtureState {
  return JSON.parse(readFileSync(TRANSFER_STATE_FILE, 'utf8')) as LibraryTransferFixtureState;
}

export function transferInstance(state: LibraryTransferFixtureState, name: string): LibraryTransferInstance {
  const instance = state.instances[name];
  if (!instance) throw new Error(`Library-transfer fixture instance '${name}' is not running.`);
  return instance;
}

function runLauncher(args: string[]): void {
  execFileSync(process.execPath, [TRANSFER_LAUNCHER, ...args], {
    stdio: 'inherit',
    timeout: 300_000,
    env: process.env,
  });
}

export function launchTransferInstance(name: string, seed: 'a' | 'b' | 'none' = 'none'): LibraryTransferInstance {
  runLauncher(['launch-instance', name, seed]);
  return transferInstance(loadTransferFixture(), name);
}

export function killTransferInstance(name: string): void {
  runLauncher(['kill-instance', name]);
}

export function restartTransferInstance(name: string, hard = false): void {
  runLauncher(hard ? ['restart-instance', name, '--hard'] : ['restart-instance', name]);
}

// --- REST helpers ---------------------------------------------------------------

export async function ltGet<T = any>(baseUrl: string, urlPath: string): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`);
  if (!res.ok) throw new Error(`GET ${urlPath} -> ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

export async function ltPost<T = any>(baseUrl: string, urlPath: string, body?: unknown): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${urlPath} -> ${res.status}: ${await res.text()}`);
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
}

export async function ltPut<T = any>(baseUrl: string, urlPath: string, body: unknown): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${urlPath} -> ${res.status}: ${await res.text()}`);
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
}

// --- library snapshots ----------------------------------------------------------

export interface BookSnapshot {
  id: string;
  title: string;
  type: string;
  hasFile: boolean;
  fileName: string | null;
  collectionIds: string[];
  progressPercent: number;
  lastLocation: string | null;
}

export interface NoteSnapshot {
  id: string;
  content: string;
  selectedText: string | null;
  cfiRange: string | null;
}

export interface LibrarySnapshot {
  books: BookSnapshot[];
  notesByBook: Record<string, NoteSnapshot[]>;
  notes: NoteSnapshot[];
  highlights: NoteSnapshot[];
  collections: Array<{ id: string; name: string }>;
  writings: Array<{ id: string; name: string; type: string; content: string | null }>;
}

export async function librarySnapshot(baseUrl: string): Promise<LibrarySnapshot> {
  const page = await ltGet<{ items: any[]; totalCount: number }>(baseUrl, '/api/books?pageSize=200');
  const books: BookSnapshot[] = page.items.map((book) => ({
    id: book.id,
    title: book.title,
    type: book.type,
    hasFile: book.hasFile === true,
    fileName: book.fileName ?? null,
    collectionIds: Array.isArray(book.collectionIds) ? book.collectionIds : [],
    progressPercent: book.progressPercent ?? 0,
    lastLocation: book.lastLocation ?? null,
  }));

  const notesByBook: Record<string, NoteSnapshot[]> = {};
  const notes: NoteSnapshot[] = [];
  for (const book of books) {
    const bookNotes = await ltGet<any[]>(baseUrl, `/api/books/${book.id}/notes`);
    notesByBook[book.id] = bookNotes.map((note) => ({
      id: note.id,
      content: note.content ?? '',
      selectedText: note.selectedText ?? null,
      cfiRange: note.cfiRange ?? null,
    }));
    notes.push(...notesByBook[book.id]);
  }

  const collections = (await ltGet<any[]>(baseUrl, '/api/collections/')).map((collection) => ({
    id: collection.id,
    name: collection.name,
  }));

  const writingSummaries = await ltGet<any[]>(baseUrl, '/api/writings/');
  const writings = [];
  for (const summary of writingSummaries) {
    const detail = await ltGet<any>(baseUrl, `/api/writings/${summary.id}`);
    writings.push({
      id: detail.id ?? summary.id,
      name: detail.name ?? summary.name,
      type: detail.type ?? summary.type,
      content: detail.content ?? null,
    });
  }

  return {
    books,
    notesByBook,
    notes,
    highlights: notes.filter((note) => (note.selectedText ?? '').length > 0),
    collections,
    writings,
  };
}

export function snapshotCounts(snapshot: LibrarySnapshot): {
  books: number;
  notes: number;
  highlights: number;
  collections: number;
  collectionMemberships: number;
} {
  return {
    books: snapshot.books.length,
    notes: snapshot.notes.length,
    highlights: snapshot.highlights.length,
    collections: snapshot.collections.length,
    collectionMemberships: snapshot.books.reduce(
      (total, book) => total + book.collectionIds.length,
      0,
    ),
  };
}

export function bookByTitle(snapshot: LibrarySnapshot, title: string): BookSnapshot {
  const book = snapshot.books.find((candidate) => candidate.title === title);
  if (!book) {
    throw new Error(
      `Book '${title}' not found. Present: ${snapshot.books.map((b) => b.title).join(', ')}`,
    );
  }
  return book;
}

/** Runs the backend reader/validator over a freshly uploaded archive. */
export interface ServerArchiveValidation {
  jobId: string;
  chunkCount: number;
  archiveBytes: number;
  sha256: string;
  preparedImport: any;
}

export async function validateArchiveOnServer(
  baseUrl: string,
  archivePath: string,
): Promise<ServerArchiveValidation> {
  const bytes = readFileSync(archivePath);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  const manifest = readPortableManifest(bytes);
  const counts = manifest.counts ?? {};
  const media: any[] = Array.isArray(manifest.media) ? manifest.media : [];
  const mediaBytes = media.reduce(
    (total, entry) => total + (typeof entry.length === 'number' ? entry.length : 0),
    0,
  );
  const dataBytes = typeof manifest.data?.length === 'number' ? manifest.data.length : 0;

  const preflight = await ltPost<any>(baseUrl, '/api/portability/migration/preflight', {
    incomingCounts: {
      works: counts.works ?? 0,
      books: counts.books ?? 0,
      notes: counts.notes ?? 0,
      topics: counts.topics ?? 0,
      noteTopics: counts.noteTopics ?? 0,
      writings: counts.writings ?? 0,
      writingNotes: counts.writingNotes ?? 0,
      collections: counts.collections ?? 0,
      collectionMemberships: counts.bookCollections ?? 0,
      acquisitions: counts.bookAcquisitions ?? 0,
      assistantSettings: counts.assistantSettings ?? 0,
      noteImportBookLinks: counts.noteImportBookLinks ?? 0,
      mediaEntries: media.length,
      totalRows: 0,
    },
    declaredArchiveBytes: bytes.length,
    declaredMediaBytes: mediaBytes,
    maxSingleEntryBytes: Math.max(dataBytes, ...media.map((entry) => entry.length ?? 0), 0),
    declaredFormatVersion: manifest.formatVersion,
    declaredDataVersion: manifest.dataVersion,
    declaredFormatName: manifest.format,
    clientDestinationRevision: null,
    isOperationalBackup: false,
  });
  if (preflight?.evaluation?.isAllowed !== true) {
    throw new Error(`Preflight rejected the archive: ${JSON.stringify(preflight?.evaluation)}`);
  }

  const created = await ltPost<any>(baseUrl, '/api/portability/migration/jobs', {
    direction: 'Import',
    idempotencyKey: randomUUID(),
    reservationId: preflight.reservationId,
  });
  const jobId = created.job.id;
  const chunkSize = preflight.chunkSizeBytes as number;
  const totalChunks = Math.ceil(bytes.length / chunkSize);

  await ltPost(baseUrl, `/api/portability/migration/jobs/${jobId}/upload-session`, {
    purpose: 'Import',
    totalBytes: bytes.length,
    chunkSize,
    totalChunks,
    fileIdentity: { totalSizeBytes: bytes.length, sha256Checksum: sha256 },
    idempotencyKey: randomUUID(),
  });

  for (let index = 0; index < totalChunks; index += 1) {
    const start = index * chunkSize;
    const end = Math.min(start + chunkSize, bytes.length) - 1;
    const slice = bytes.subarray(start, end + 1);
    const res = await fetch(
      `${baseUrl}/api/portability/migration/jobs/${jobId}/upload-session/chunks/${index}`,
      {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/octet-stream',
          'Content-Range': `bytes ${start}-${end}/${bytes.length}`,
          'X-Nostos-Chunk-SHA256': createHash('sha256').update(slice).digest('hex'),
        },
        body: slice,
      },
    );
    if (!res.ok) throw new Error(`chunk ${index} -> ${res.status}: ${await res.text()}`);
  }

  await ltPost(baseUrl, `/api/portability/migration/jobs/${jobId}/upload-session/complete`);

  const status = await waitForJobState(baseUrl, jobId, ['ReadyToActivate', 'Completed'], 180_000);
  return {
    jobId,
    chunkCount: totalChunks,
    archiveBytes: bytes.length,
    sha256,
    preparedImport: status.preparedImport,
  };
}

export async function waitForJobState(
  baseUrl: string,
  jobId: string,
  states: string[],
  timeoutMs = 120_000,
): Promise<any> {
  const deadline = Date.now() + timeoutMs;
  let last: any = null;
  while (Date.now() < deadline) {
    const status = await ltGet<any>(baseUrl, `/api/portability/migration/jobs/${jobId}`);
    last = status;
    if (states.includes(status.job.state)) return status;
    if (status.job.state === 'Failed' || status.job.state === 'Cancelled') {
      throw new Error(
        `Job ${jobId} ended '${status.job.state}' (${status.job.failureCode}: ${status.job.failureMessage})`,
      );
    }
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
  throw new Error(`Job ${jobId} did not reach ${states.join('/')} in ${timeoutMs}ms (last ${last?.job?.state})`);
}

/** Small central-directory reader: extracts manifest.json from a portable ZIP. */
export function readPortableManifest(zip: Buffer): any {
  const eocd = findEndOfCentralDirectory(zip);
  const entryCount = zip.readUInt16LE(eocd + 10);
  const centralOffset = zip.readUInt32LE(eocd + 16);
  let offset = centralOffset;
  for (let i = 0; i < entryCount; i += 1) {
    if (zip.readUInt32LE(offset) !== 0x02014b50) {
      throw new Error(`Malformed central directory at entry ${i}.`);
    }
    const method = zip.readUInt16LE(offset + 10);
    const compressedSize = zip.readUInt32LE(offset + 20);
    const nameLength = zip.readUInt16LE(offset + 28);
    const extraLength = zip.readUInt16LE(offset + 30);
    const commentLength = zip.readUInt16LE(offset + 32);
    const localOffset = zip.readUInt32LE(offset + 42);
    const name = zip.subarray(offset + 46, offset + 46 + nameLength).toString('utf8');
    if (name.toLowerCase() === 'manifest.json') {
      return JSON.parse(inflateZipEntry(zip, localOffset, method, compressedSize).toString('utf8'));
    }
    offset += 46 + nameLength + extraLength + commentLength;
  }
  throw new Error('manifest.json not found in archive.');
}

function findEndOfCentralDirectory(zip: Buffer): number {
  const minimum = Math.max(0, zip.length - 65_557);
  for (let offset = zip.length - 22; offset >= minimum; offset -= 1) {
    if (zip.readUInt32LE(offset) === 0x06054b50) return offset;
  }
  throw new Error('EOCD not found.');
}

function inflateZipEntry(zip: Buffer, localOffset: number, method: number, compressedSize: number): Buffer {
  if (zip.readUInt32LE(localOffset) !== 0x04034b50) throw new Error('Malformed local header.');
  const nameLength = zip.readUInt16LE(localOffset + 26);
  const extraLength = zip.readUInt16LE(localOffset + 28);
  const start = localOffset + 30 + nameLength + extraLength;
  const data = zip.subarray(start, start + compressedSize);
  if (method === 0) return Buffer.from(data);
  if (method === 8) return inflateRawSync(data);
  throw new Error(`Unsupported ZIP compression method ${method}.`);
}

/** Fallback archive producer for specs that run after the UI export scenario. */
export async function exportArchiveViaApi(
  baseUrl: string,
  destinationPath: string,
): Promise<{ jobId: string; archiveBytes: number; sha256: string }> {
  const created = await ltPost<any>(baseUrl, '/api/portability/migration/jobs', {
    direction: 'Export',
    idempotencyKey: randomUUID(),
  });
  const jobId = created.job.id;
  await waitForJobState(baseUrl, jobId, ['Completed'], 180_000);
  const res = await fetch(`${baseUrl}/api/portability/migration/jobs/${jobId}/export-download`);
  if (!res.ok) throw new Error(`export-download -> ${res.status}: ${await res.text()}`);
  const bytes = Buffer.from(await res.arrayBuffer());
  writeFileSync(destinationPath, bytes);
  return {
    jobId,
    archiveBytes: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  };
}

export function sha256File(filePath: string): string {
  return createHash('sha256').update(readFileSync(filePath)).digest('hex');
}

export function writeTransferArtifact(name: string, value: unknown): string {
  const file = path.join(TRANSFER_ARTIFACTS, name);
  writeFileSync(file, JSON.stringify(value, null, 2));
  return file;
}
