import { TransferResumeStore, TRANSFER_RESUME_STORAGE_KEY } from './transfer-resume-store.service';
import { PersistedTransferResumeState } from '../models/library-transfer.models';

function record(overrides: Partial<PersistedTransferResumeState> = {}): PersistedTransferResumeState {
  return {
    schemaVersion: 1,
    jobId: 'job-1',
    sessionId: 'session-1',
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

  it('persists only operational metadata, never file bytes, URLs or credentials', () => {
    store.save(record());
    const raw = localStorage.getItem(TRANSFER_RESUME_STORAGE_KEY)!;

    expect(raw).not.toMatch(/blob:|objectURL|signed|token|password|credential/i);
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    const flat = JSON.stringify(parsed);
    expect(flat).not.toContain('data:');
    expect(Object.keys(parsed).sort()).toEqual(
      [
        'createdAt',
        'direction',
        'fileIdentity',
        'fileName',
        'jobId',
        'preflightRequest',
        'schemaVersion',
        'sessionId',
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

  it('returns null when updating without a record', () => {
    expect(store.update({ sessionId: 'none' })).toBeNull();
  });
});
