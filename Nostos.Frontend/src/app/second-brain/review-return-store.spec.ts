import { ReviewReturnStore, type ReviewReturnState } from './review-return-store';

const state: ReviewReturnState = {
  returnMode: 'notes',
  searchQuery: 'virtue',
  selectedId: null,
  noteSearchQuery: '',
  sourceFilter: 'all',
  noteSort: 'newest',
  browseQuery: 'stoic',
  browseBookId: 'b1',
  browseBookTitle: 'Meditations',
  browseWithoutTopics: true,
  browseOldestFirst: false,
  focusedNoteId: 'n1',
  reviewId: 'n2',
};
const KEY = 'nostos.brain.reviewReturn.v1:account%3A42';

describe('ReviewReturnStore', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it('keeps nothing until it knows whose state it is', () => {
    const store = new ReviewReturnStore();

    store.write(state);

    expect(sessionStorage.length).toBe(0);
    expect(store.read()).toBeNull();
  });

  it('stores under a key that names the owner, and reads it back', () => {
    const store = new ReviewReturnStore();
    expect(store.setIdentity('account:42')).toEqual({ changed: true, hadPrevious: false, hasCurrent: true });

    store.write(state);

    expect(sessionStorage.getItem(KEY)).not.toBeNull();
    expect(store.read()).toEqual(state);
  });

  it('reports no change when told the same identity again', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');

    expect(store.setIdentity('account:42').changed).toBe(false);
  });

  it('discards one account’s state when another takes over', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');
    store.write(state);

    expect(store.setIdentity('account:7')).toEqual({ changed: true, hadPrevious: true, hasCurrent: true });

    expect(sessionStorage.getItem(KEY)).toBeNull();
    expect(store.read()).toBeNull();
  });

  it('discards its state on sign-out and keeps nothing afterwards', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');
    store.write(state);

    expect(store.setIdentity(null)).toEqual({ changed: true, hadPrevious: true, hasCurrent: false });
    store.write(state);

    expect(sessionStorage.length).toBe(0);
  });

  it('throws away stored state that is not the expected shape', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');
    sessionStorage.setItem(KEY, JSON.stringify({ ...state, noteSort: 'sideways' }));

    expect(store.read()).toBeNull();
    expect(sessionStorage.getItem(KEY)).toBeNull();
  });

  it('reads unparseable text as nothing', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');
    sessionStorage.setItem(KEY, '{not json');

    expect(store.read()).toBeNull();
  });

  it('clears on request', () => {
    const store = new ReviewReturnStore();
    store.setIdentity('account:42');
    store.write(state);

    store.clear();

    expect(store.read()).toBeNull();
  });
});
