/**
 * Slice B7 integration: the real bounded-memory chunk engine driving the real
 * HTTP transport, with `HttpTestingController` playing the merged #679 server.
 * The server side asserts the exact headers per request; the client asserts
 * progress and that a resumed session uploads only the missing chunks.
 */

import { HttpClient, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { MigrationSessionStatusDto } from '../models/migration-http.dtos';
import { TransferProgress } from '../models/library-transfer.models';
import { ChunkUploadEngine } from './chunk-upload-engine.service';
import { FileDigestService } from './file-digest.service';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import { sha256ChunkHex } from './hash/chunk-digest';
import {
  CHUNK_HASH_HEADER_NAME,
  HttpLibraryTransferTransport,
  MIGRATION_BASE_PATH,
} from './http-library-transfer-transport';

const JOB_ID = '11111111-1111-4111-8111-111111111111';
const SESSION_ID = '22222222-2222-4222-8222-222222222222';
const CHUNK = 4 * 1024 * 1024;
const SESSION_URL = `${MIGRATION_BASE_PATH}/jobs/${JOB_ID}/upload-session`;

function fileOfSize(size: number): File {
  const bytes = new Uint8Array(size);
  for (let index = 0; index < size; index += 1) bytes[index] = (index * 31) % 256;
  return new File([bytes as unknown as BlobPart], 'library.nostos');
}

function sessionStatus(
  totalBytes: number,
  receivedChunks: number[] = [],
): MigrationSessionStatusDto {
  return {
    sessionId: SESSION_ID,
    purpose: 'Import',
    state: receivedChunks.length > 0 ? 'Receiving' : 'Created',
    totalBytes,
    chunkSize: CHUNK,
    totalChunks: Math.ceil(totalBytes / CHUNK),
    fileIdentity: { totalSizeBytes: totalBytes, sha256Checksum: 'a'.repeat(64) },
    receivedChunks,
    receivedChunkCount: receivedChunks.length,
    createdAtUtc: new Date(0).toISOString(),
    expiresAtUtc: new Date(86_400_000).toISOString(),
  };
}

describe('HttpLibraryTransferTransport + ChunkUploadEngine', () => {
  let http: HttpTestingController;
  let transport: HttpLibraryTransferTransport;
  let digest: FileDigestService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: HASH_WORKER_FACTORY, useValue: () => null },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    transport = new HttpLibraryTransferTransport(TestBed.inject(HttpClient));
    digest = TestBed.inject(FileDigestService);
  });

  afterEach(() => {
    http.verify({ ignoreCancelled: true });
    TestBed.resetTestingModule();
  });

  it('uploads every chunk of a fresh session through the real HTTP transport', async () => {
    const file = fileOfSize(CHUNK + 7);
    const seeded = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush({
      session: sessionStatus(file.size),
      receivedRanges: [],
    });
    const session = (await seeded).session;

    const engine = new ChunkUploadEngine(transport, digest);
    const progress: TransferProgress[] = [];
    const run = engine.start({
      jobId: JOB_ID,
      session,
      file,
      onProgress: (update) => progress.push(update),
    });

    const first = await waitForPut(http, `${SESSION_URL}/chunks/0`);
    const second = await waitForPut(http, `${SESSION_URL}/chunks/1`);

    for (const [index, request] of [first, second].entries()) {
      const slice = file.slice(index * CHUNK, Math.min((index + 1) * CHUNK, file.size));
      expect(request.request.headers.get(CHUNK_HASH_HEADER_NAME)).toBe(
        await sha256ChunkHex(slice),
      );
      expect(request.request.headers.get('Content-Range')).toBe(
        `bytes ${index * CHUNK}-${index * CHUNK + slice.size - 1}/${file.size}`,
      );
      request.flush({ sessionId: SESSION_ID, chunkIndex: index, alreadyPresent: false });
    }

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(progress.at(-1)).toMatchObject({
      uploadedBytes: file.size,
      completedChunks: 2,
      totalChunks: 2,
      inFlightBytes: 0,
    });
  });

  it('resumes from the server ranges and uploads only the missing chunks', async () => {
    const file = fileOfSize(CHUNK * 2);
    const seeded = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush({
      session: sessionStatus(file.size),
      // Server-authoritative ranges: chunk 0 already has a receipt.
      receivedRanges: [{ startIndex: 0, endIndex: 0 }],
    });
    const session = (await seeded).session;

    const engine = new ChunkUploadEngine(transport, digest);
    const run = engine.start({ jobId: JOB_ID, session, file });

    // Durable progress starts from the received chunk, not from zero.
    expect(run.progress.uploadedBytes).toBe(CHUNK);
    expect(run.progress.completedChunks).toBe(1);

    const put = await waitForPut(http, `${SESSION_URL}/chunks/1`);
    expect(http.match({ method: 'PUT', url: `${SESSION_URL}/chunks/0` })).toHaveLength(0);
    put.flush({ sessionId: SESSION_ID, chunkIndex: 1, alreadyPresent: false });

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(run.progress).toMatchObject({
      uploadedBytes: file.size,
      completedChunks: 2,
    });
  });
});

async function waitForPut(http: HttpTestingController, url: string): Promise<TestRequest> {
  for (let attempt = 0; attempt < 50; attempt += 1) {
    const found = http.match({ method: 'PUT', url });
    if (found.length > 0) return found[0];
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  return http.expectOne({ method: 'PUT', url });
}
