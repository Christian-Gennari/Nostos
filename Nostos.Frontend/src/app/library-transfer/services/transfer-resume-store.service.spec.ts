import { TransferResumeStore, TRANSFER_RESUME_STORAGE_KEY } from './transfer-resume-store.service';
import { PersistedTransferResumeState } from '../models/library-transfer.models';

function record(overrides: Partial<PersistedTransferResumeState> = {}): PersistedTransferResumeState {
  return {
    schemaVersion: 1,
    jobId: 'job-1',
    jobCreationIdempotencyKey: 'job-key-1',
    reservationId: 'reservation-1',
    sessionId: 'session-1',
    sessionCreationIdempotencyKey: 'session-key-1',
    sessionRequest: {
      purpose: 'Import',
      totalBytes: 1024,
      chunkSize: 4 * 1024 * 1024,
      totalChunks: 1,
      fileIdentity: {
        totalSizeBytes: 1024,
        sha256Checksum: 'a'.repeat(64),
        clientFingerprint: 'nostos-fp-v1:' + 'b'.repeat(64),
      },
      idempotencyKey: 'session-key-1',
    },
    chunkSizeBytes: 4 * 1024 * 1024,
    direction: 'import',
    fileName: 'library.nostos',
    createdAt: '2026-10-01T00:00:00.000Z',
    fileIdentity: {
      totalSizeBytes: 1024,
      sha256Checksum: 'a'.repeat(64),
      clientFingerprint: 'nostos-fp-v1:' + 'b'.repeat(64),
    },
    preflightRequest: {
      incomingCounts: {
        works: 0,
        books: 0,
        notes: 0,
        topics: 0,
        noteTopics: 0,
        writings: 0,
        writingNotes: 0,
        collections: 0,
        collectionMemberships: 0,
        acquisitions: 0,
        assistantSettings: 0,
        noteImportBookLinks: 0,
        mediaEntries: 0,
        totalRows: 0,
      },
      declaredArchiveBytes: 1024,
      declaredMediaBytes: 0,
      maxSingleEntryBytes: 0,
      declaredFormatVersion: 1,
      declaredDataVersion: 3,
      declaredFormatName: 'nostos-portable',
      clientDestinationRevision: null,
      isOperationalBackup: false,
    },
    ...overrides,
  };
}

describe('TransferResumeStore', () => {
  const store = new TransferResumeStore();

  beforeEach(() => localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY));
  afterEach(() => localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY));

  it('round-trips a record under the versioned key', () => {
    store.save(record());
    const raw = localStorage.getItem(TRANSFER_RESUME_STORAGE_KEY);
    expect(raw).toBeTruthy();
    expect(JSON.parse(raw!)).toEqual(record());
    expect(store.load()).toEqual(record());
  });

  it('retains a discovered server job without inventing a preflight request', () => {
    const discovered = record({ serverDiscovered: true, preflightRequest: undefined });
    store.save(discovered);
    expect(store.load()).toEqual(discovered);
    store.save(record({ preflightRequest: undefined }));
    expect(store.load()).toBeNull();
    store.save(record({ serverDiscovered: true, preflightRequest: undefined, jobId: undefined }));
    expect(store.load()).toBeNull();
  });

  it('persists only operational metadata, never file bytes, URLs or credentials', () => {
    store.save(record());
    const raw = localStorage.getItem(TRANSFER_RESUME_STORAGE_KEY)!;

    expect(raw).not.toMatch(/blob:|objectURL|signed|token|password|credential/i);
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    const flat = JSON.stringify(parsed);
    expect(flat).not.toContain('data:');
    expect(Object.keys(parsed).sort()).toEqual(
      [
        'chunkSizeBytes',
        'createdAt',
        'direction',
        'fileIdentity',
        'fileName',
        'jobCreationIdempotencyKey',
        'jobId',
        'preflightRequest',
        'reservationId',
        'schemaVersion',
        'sessionCreationIdempotencyKey',
        'sessionId',
        'sessionRequest',
      ].sort(),
    );
  });

  it('updates a record in place', () => {
    store.save(record());
    store.update({ sessionId: 'session-2' });
    expect(store.load()?.sessionId).toBe('session-2');
    expect(store.load()?.jobId).toBe('job-1');
  });

  it('clears the record', () => {
    store.save(record());
    store.clear();
    expect(store.load()).toBeNull();
    expect(localStorage.getItem(TRANSFER_RESUME_STORAGE_KEY)).toBeNull();
  });

  it('drops corrupt JSON instead of failing', () => {
    localStorage.setItem(TRANSFER_RESUME_STORAGE_KEY, '{not json');
    expect(store.load()).toBeNull();
    expect(localStorage.getItem(TRANSFER_RESUME_STORAGE_KEY)).toBeNull();
  });

  it('rejects records with an unknown schema or missing identity', () => {
    localStorage.setItem(
      TRANSFER_RESUME_STORAGE_KEY,
      JSON.stringify({ schemaVersion: 2, jobId: 'x', direction: 'import' }),
    );
    expect(store.load()).toBeNull();
  });

  it('rejects a record without a job-creation idempotency key', () => {
    const withoutKey = { ...record() } as Record<string, unknown>;
    delete withoutKey['jobCreationIdempotencyKey'];
    localStorage.setItem(TRANSFER_RESUME_STORAGE_KEY, JSON.stringify(withoutKey));
    expect(store.load()).toBeNull();
  });

  it('accepts a provisional record whose jobId has not been promoted yet', () => {
    const provisional = { ...record() } as Record<string, unknown>;
    delete provisional['jobId'];
    delete provisional['sessionId'];
    store.save(provisional as unknown as PersistedTransferResumeState);
    const loaded = store.load();
    expect(loaded?.jobId).toBeUndefined();
    expect(loaded?.jobCreationIdempotencyKey).toBe('job-key-1');
  });

  it('returns null when updating without a record', () => {
    expect(store.update({ sessionId: 'none' })).toBeNull();
  });
});
