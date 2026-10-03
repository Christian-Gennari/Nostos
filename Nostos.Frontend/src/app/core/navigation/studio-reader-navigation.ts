export const STUDIO_SOURCE_RETURN_STATE_KEY = 'nostosStudioSourceReturn';
export const READER_RETURN_ORIGIN_STATE_KEY = 'nostosReaderReturnOrigin';

export interface StudioSourceReturnSnapshotV1 {
  version: 1;
  writingId: string;
  editor?: {
    bookmark?: unknown;
    scrollY?: number;
  };
  references?: {
    mode: 'writing' | 'library';
    activeLibraryTab: 'brain' | 'notes';
    wasOpen: boolean;
    inspectedSourceId?: string | null;
    selectedTopicId?: string | null;
    selectedBookId?: string | null;
  };
}

export interface ReaderReturnOriginV1 {
  version: 1;
  kind: 'studio';
  writingId: string;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
}

function isOptionalString(value: unknown): value is string | null | undefined {
  return value === null || value === undefined || typeof value === 'string';
}

export function readStudioSourceReturnSnapshot(
  state: unknown,
): StudioSourceReturnSnapshotV1 | null {
  const raw = asRecord(state)[STUDIO_SOURCE_RETURN_STATE_KEY];
  if (!raw || typeof raw !== 'object') return null;

  const snapshot = raw as Record<string, unknown>;
  if (snapshot['version'] !== 1) return null;
  const writingId = typeof snapshot['writingId'] === 'string' ? snapshot['writingId'].trim() : '';
  if (!writingId) return null;

  const result: StudioSourceReturnSnapshotV1 = { version: 1, writingId };
  const editor = snapshot['editor'];
  if (editor && typeof editor === 'object') {
    const rawEditor = editor as Record<string, unknown>;
    const scrollY = rawEditor['scrollY'];
    result.editor = {
      ...(rawEditor['bookmark'] !== undefined ? { bookmark: rawEditor['bookmark'] } : {}),
      ...(typeof scrollY === 'number' && Number.isFinite(scrollY) && scrollY >= 0
        ? { scrollY }
        : {}),
    };
  }

  const references = snapshot['references'];
  if (references && typeof references === 'object') {
    const rawReferences = references as Record<string, unknown>;
    const mode = rawReferences['mode'];
    const activeLibraryTab = rawReferences['activeLibraryTab'];
    const wasOpen = rawReferences['wasOpen'];
    const inspectedSourceId = rawReferences['inspectedSourceId'];
    const selectedTopicId = rawReferences['selectedTopicId'];
    const selectedBookId = rawReferences['selectedBookId'];

    if (
      (mode === 'writing' || mode === 'library') &&
      (activeLibraryTab === 'brain' || activeLibraryTab === 'notes') &&
      typeof wasOpen === 'boolean' &&
      isOptionalString(inspectedSourceId) &&
      isOptionalString(selectedTopicId) &&
      isOptionalString(selectedBookId)
    ) {
      result.references = {
        mode,
        activeLibraryTab,
        wasOpen,
        ...(inspectedSourceId !== undefined ? { inspectedSourceId } : {}),
        ...(selectedTopicId !== undefined ? { selectedTopicId } : {}),
        ...(selectedBookId !== undefined ? { selectedBookId } : {}),
      };
    }
  }

  return result;
}

export function hasStudioSourceReturnState(state: unknown): boolean {
  return Object.prototype.hasOwnProperty.call(asRecord(state), STUDIO_SOURCE_RETURN_STATE_KEY);
}

export function withStudioSourceReturnState(
  state: unknown,
  snapshot: StudioSourceReturnSnapshotV1,
): Record<string, unknown> {
  return {
    ...asRecord(state),
    [STUDIO_SOURCE_RETURN_STATE_KEY]: snapshot,
  };
}

export function withoutStudioSourceReturnState(state: unknown): Record<string, unknown> {
  const next = { ...asRecord(state) };
  delete next[STUDIO_SOURCE_RETURN_STATE_KEY];
  return next;
}

export function readerReturnOriginState(
  writingId: string,
): Record<string, ReaderReturnOriginV1> {
  return {
    [READER_RETURN_ORIGIN_STATE_KEY]: {
      version: 1,
      kind: 'studio',
      writingId,
    },
  };
}

export function readReaderReturnOrigin(state: unknown): ReaderReturnOriginV1 | null {
  const raw = asRecord(state)[READER_RETURN_ORIGIN_STATE_KEY];
  if (!raw || typeof raw !== 'object') return null;

  const origin = raw as Record<string, unknown>;
  const writingId = typeof origin['writingId'] === 'string' ? origin['writingId'].trim() : '';
  if (origin['version'] !== 1 || origin['kind'] !== 'studio' || !writingId) return null;

  return { version: 1, kind: 'studio', writingId };
}
