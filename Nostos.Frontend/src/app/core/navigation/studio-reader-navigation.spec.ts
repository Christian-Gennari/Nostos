import { describe, expect, it } from 'vitest';

import {
  READER_RETURN_ORIGIN_STATE_KEY,
  STUDIO_SOURCE_RETURN_STATE_KEY,
  StudioSourceReturnSnapshotV1,
  hasStudioSourceReturnState,
  readReaderReturnOrigin,
  readStudioSourceReturnSnapshot,
  readerReturnOriginState,
  withStudioSourceReturnState,
  withoutStudioSourceReturnState,
} from './studio-reader-navigation';

const SNAPSHOT: StudioSourceReturnSnapshotV1 = {
  version: 1,
  writingId: 'w1',
  editor: { bookmark: { from: 3 }, scrollY: 420 },
  references: {
    mode: 'library',
    activeLibraryTab: 'brain',
    wasOpen: true,
    inspectedSourceId: 'n1',
    selectedTopicId: 't1',
    selectedBookId: 'b1',
  },
};

describe('studio source return snapshot', () => {
  it('reads a snapshot the Studio wrote', () => {
    expect(readStudioSourceReturnSnapshot({ [STUDIO_SOURCE_RETURN_STATE_KEY]: SNAPSHOT })).toEqual(
      SNAPSHOT,
    );
  });

  it('reads a snapshot without its optional parts', () => {
    const minimal = { version: 1 as const, writingId: 'w1' };

    expect(
      readStudioSourceReturnSnapshot({ [STUDIO_SOURCE_RETURN_STATE_KEY]: minimal }),
    ).toEqual(minimal);
  });

  it.each([
    ['no state at all', undefined],
    ['an empty state', {}],
    ['a snapshot that is not an object', 'nope'],
    ['another version', { version: 2, writingId: 'w1' }],
    ['no writing id', { version: 1 }],
    ['a blank writing id', { version: 1, writingId: '   ' }],
  ])('reads nothing from %s', (_case, raw) => {
    expect(readStudioSourceReturnSnapshot({ [STUDIO_SOURCE_RETURN_STATE_KEY]: raw })).toBeNull();
  });

  it('keeps a valid bookmark and a non-negative scroll', () => {
    const snapshot = readStudioSourceReturnSnapshot({
      [STUDIO_SOURCE_RETURN_STATE_KEY]: {
        version: 1,
        writingId: 'w1',
        editor: { bookmark: 'pos', scrollY: 0 },
      },
    });

    expect(snapshot?.editor).toEqual({ bookmark: 'pos', scrollY: 0 });
  });

  it('drops a scroll that is not a real position', () => {
    const snapshot = readStudioSourceReturnSnapshot({
      [STUDIO_SOURCE_RETURN_STATE_KEY]: {
        version: 1,
        writingId: 'w1',
        editor: { bookmark: 'pos', scrollY: -1 },
      },
    });

    expect(snapshot?.editor).toEqual({ bookmark: 'pos' });
  });

  it('drops the whole references block when any part of it is unreadable', () => {
    const snapshot = readStudioSourceReturnSnapshot({
      [STUDIO_SOURCE_RETURN_STATE_KEY]: {
        version: 1,
        writingId: 'w1',
        references: { mode: 'library', activeLibraryTab: 'brain', wasOpen: 'yes' },
      },
    });

    expect(snapshot?.references).toBeUndefined();
  });

  it('keeps a references block whose nullable ids are explicitly empty', () => {
    const snapshot = readStudioSourceReturnSnapshot({
      [STUDIO_SOURCE_RETURN_STATE_KEY]: {
        version: 1,
        writingId: 'w1',
        references: {
          mode: 'writing',
          activeLibraryTab: 'notes',
          wasOpen: false,
          inspectedSourceId: null,
          selectedTopicId: null,
          selectedBookId: null,
        },
      },
    });

    expect(snapshot?.references).toEqual({
      mode: 'writing',
      activeLibraryTab: 'notes',
      wasOpen: false,
      inspectedSourceId: null,
      selectedTopicId: null,
      selectedBookId: null,
    });
  });

  it('tells whether a state carries the snapshot, and adds or removes it', () => {
    expect(hasStudioSourceReturnState({})).toBe(false);
    expect(hasStudioSourceReturnState({ [STUDIO_SOURCE_RETURN_STATE_KEY]: SNAPSHOT })).toBe(true);

    const withIt = withStudioSourceReturnState({ other: 1 }, SNAPSHOT);
    expect(withIt).toEqual({ other: 1, [STUDIO_SOURCE_RETURN_STATE_KEY]: SNAPSHOT });

    expect(withoutStudioSourceReturnState(withIt)).toEqual({ other: 1 });
    // The state it was given is not changed.
    expect(withIt).toHaveProperty(STUDIO_SOURCE_RETURN_STATE_KEY);
  });
});

describe('reader return origin', () => {
  it('round-trips the writing a reader was opened from', () => {
    const state = readerReturnOriginState('w1');

    expect(readReaderReturnOrigin(state)).toEqual({ version: 1, kind: 'studio', writingId: 'w1' });
  });

  it.each([
    ['no state', undefined],
    ['an empty state', {}],
    ['another version', { version: 2, kind: 'studio', writingId: 'w1' }],
    ['another origin', { version: 1, kind: 'library', writingId: 'w1' }],
    ['a blank writing', { version: 1, kind: 'studio', writingId: ' ' }],
  ])('reads no origin from %s', (_case, raw) => {
    expect(readReaderReturnOrigin({ [READER_RETURN_ORIGIN_STATE_KEY]: raw })).toBeNull();
  });
});
