import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

// Sigma requires WebGL2 which is unavailable in the test environment.
vi.mock('sigma', () => {
  class MockSigma {
    constructor() {}
    on() { return this; }
    setSetting() {}
    refresh() {}
    kill() {}
    getCamera() {
      return {
        x: 0.5,
        y: 0.5,
        angle: 0,
        ratio: 1,
        animatedReset: vi.fn(),
        animatedZoom: vi.fn(),
        animatedUnzoom: vi.fn(),
        animate: vi.fn(),
        setState: vi.fn(),
        getState: vi.fn(() => ({ x: 0.5, y: 0.5, angle: 0, ratio: 1 })),
        disable: vi.fn(),
        enable: vi.fn(),
      };
    }
    getMouseCaptor() {
      return { on: vi.fn().mockReturnThis() };
    }
    getDimensions() {
      return { width: 800, height: 600 };
    }
    viewportToGraph(coords: { x: number; y: number }) {
      return coords;
    }
  }
  return { default: MockSigma };
});

vi.mock('graphology', () => {
  class MockGraph {
    private nodes = new Map<string, Record<string, unknown>>();
    private edges: Array<{ key: string; source: string; target: string; attrs: Record<string, unknown> }> = [];
    addNode(key: string, attrs: Record<string, unknown> = {}) { this.nodes.set(key, attrs); }
    addEdge(source: string, target: string, attrs: Record<string, unknown> = {}) {
      const key = `${source}->${target}`;
      this.edges.push({ key, source, target, attrs });
      return key;
    }
    get order() { return this.nodes.size; }
    get size() { return this.edges.length; }
    forEachNode(cb: (n: string, a: Record<string, unknown>) => void) {
      for (const [n, a] of this.nodes) cb(n, a);
    }
    setNodeAttribute(n: string, attribute: string, value: unknown) {
      this.nodes.get(n)![attribute] = value;
    }
    forEachEdge(cb: (e: string, a: Record<string, unknown>, s: string, t: string) => void) {
      for (const e of this.edges) cb(e.key, e.attrs, e.source, e.target);
    }
    forEachNeighbor(node: string, cb: (n: string) => void) {
      for (const e of this.edges) {
        if (e.source === node) cb(e.target);
        else if (e.target === node) cb(e.source);
      }
    }
    source(edge: string) { return this.edges.find(e => e.key === edge)?.source ?? ''; }
    target(edge: string) { return this.edges.find(e => e.key === edge)?.target ?? ''; }
  }
  return { default: MockGraph };
});

/**
 * d3-force is the topic map's layout engine now (it replaced ForceAtlas2).
 * This spec mounts the map inside the page, so the simulation must exist in
 * jsdom where there is no WebGL and no rAF-driven physics.
 */
vi.mock('d3-force', () => {
  class MockSimulation {
    private nodesArray: Array<Record<string, unknown>> = [];
    constructor(nodes: Array<Record<string, unknown>> = []) { this.nodesArray = nodes; }
    nodes() { return this.nodesArray; }
    force() { return this; }
    alpha() { return 0.001; }
    alphaTarget() { return this; }
    velocityDecay() { return this; }
    alphaDecay() { return this; }
    alphaMin() { return 0.001; }
    tick() { return this; }
    stop() { return this; }
    restart() { return this; }
    on() { return this; }
  }
  const chainable = () => {
    const self: Record<string, unknown> = {};
    for (const m of ['strength', 'distance', 'id', 'radius', 'distanceMin', 'x', 'y']) {
      self[m] = vi.fn(() => self);
    }
    return self;
  };
  return {
    forceSimulation: vi.fn((nodes: unknown) => new MockSimulation(nodes as Array<Record<string, unknown>>)),
    forceX: vi.fn(() => chainable()),
    forceY: vi.fn(() => chainable()),
    forceLink: vi.fn(() => chainable()),
    forceManyBody: vi.fn(() => chainable()),
    forceCollide: vi.fn(() => chainable()),
  };
});

import { SecondBrain } from './second-brain.component';
import { TopicDetailDto, TopicDto, TopicStatsDto } from '../core/services/topics.service';
import { NoteSearchHit } from '../core/dtos/note.dtos';
import { ToastService } from '../core/services/toast.service';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService } from '../ui/assistant/assistant.service';

/**
 * Second Brain behaviour that the "flashing" complaint was about.
 *
 * The page's background is identical for every topic, so the detail pane must
 * swap its content WITHOUT a loading surface and WITHOUT an arrival animation.
 * These specs pin the two mechanisms that made it flash: the `loadingDetail`
 * bit that used to drive a covering wait-field, and the re-fetch of an
 * already-seen topic.
 */
const topics: TopicDto[] = [
  { id: 'c-alpha', name: 'Alpha', usageCount: 9 },
  { id: 'c-beta', name: 'Beta', usageCount: 3 },
  { id: 'c-gamma', name: 'Gamma', usageCount: 3 },
];

const stats: TopicStatsDto = {
  totalTopics: 3,
  totalReferences: 15,
  singleNoteTopics: 0,
  mostUsedName: 'Alpha',
  mostUsedCount: 9,
};

const detail = (id: string, name: string): TopicDetailDto => ({
  id,
  name,
  notes: [
    {
      noteId: `${id}-n1`,
      content: `A note about [[${name}]]`,
      selectedText: undefined,
      cfiRange: undefined,
      bookId: 'b1',
      bookTitle: 'Meditations',
    },
  ],
});

const detailWithNotes = (id: string, name: string): TopicDetailDto => ({
  id,
  name,
  notes: [
    {
      noteId: `${id}-newest`,
      content: `Newest thought about [[${name}]]`,
      selectedText: undefined,
      cfiRange: 'epubcfi(/6/2)',
      bookId: 'b-ideas',
      bookTitle: 'Ideas in Motion',
      createdAt: '2026-09-10T12:00:00Z',
    },
    {
      noteId: `${id}-oldest`,
      content: `Oldest thought about [[${name}]]`,
      selectedText: 'An old passage',
      cfiRange: 'epubcfi(/6/4)',
      bookId: 'b-meditations',
      bookTitle: 'Meditations',
      createdAt: '2026-09-01T12:00:00Z',
    },
    {
      noteId: `${id}-middle`,
      content: `A middle thought about [[${name}]]`,
      selectedText: undefined,
      cfiRange: undefined,
      bookId: 'b-ideas',
      bookTitle: 'Ideas in Motion',
      createdAt: '2026-09-05T12:00:00Z',
    },
  ],
});

const detailWithFourNotes = (id: string, name: string): TopicDetailDto => {
  const base = detailWithNotes(id, name);
  return {
    ...base,
    notes: [...base.notes, {
      noteId: `${id}-fourth`,
      content: `A fourth thought about [[${name}]]`,
      selectedText: undefined,
      cfiRange: undefined,
      bookId: 'b-fourth',
      bookTitle: 'A Fourth Book',
      createdAt: '2026-09-12T12:00:00Z',
    }],
  };
};

describe('SecondBrain', () => {
  let component: SecondBrain;
  let fixture: ComponentFixture<SecondBrain>;
  let http: HttpTestingController;

  const flushChildTopicLists = (): void => {
    for (const request of http.match('/api/topics')) {
      if (!request.cancelled) request.flush(topics);
    }
  };

  const flushRelated = (id: string, related: object[] = []): void => {
    http.expectOne(`/api/topics/${id}/related`).flush(related);
    fixture.detectChanges();
    flushChildTopicLists();
  };

  /**
   * Lets the debounced note-text search fire and answers it (issue #158). The
   * debounce is 250ms in the component; the slack keeps the helper stable without
   * reaching into a private field.
   */
  const settleNoteSearch = async (
    rows: TopicDto[] = [],
    noteHits: NoteSearchHit[] = []
  ): Promise<void> => {
    await new Promise((resolve) => setTimeout(resolve, 320));
    http
      .match((request) => request.url === '/api/topics' && request.params.has('search'))
      .forEach((request) => request.flush(rows));
    http
      .match((request) => request.url === '/api/notes/search')
      .forEach((request) => request.flush(noteHits));
    fixture.detectChanges();
    await fixture.whenStable();
  };

  const flushDetail = (id: string, value: TopicDetailDto, related: object[] = []): void => {
    http.expectOne(`/api/topics/${id}`).flush(value);
    flushRelated(id, related);
  };

  const flushMutationRefresh = (
    refreshedTopics: TopicDto[] = topics,
    refreshedStats: TopicStatsDto = stats
  ): void => {
    // Opening a note editor creates a self-contained topic autocomplete
    // input, which also requests the list. The final list request is the
    // mutation refresh; settle any earlier child requests first.
    const listRequests = http.match('/api/topics');
    expect(listRequests.length).toBeGreaterThan(0);
    listRequests.slice(0, -1).forEach((request) => request.flush(topics));
    listRequests.at(-1)!.flush(refreshedTopics);
    http.expectOne('/api/topics/stats').flush(refreshedStats);
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SecondBrain],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(SecondBrain);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').flush(stats);

    await fixture.whenStable();
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  describe('Notes browsing (#590)', () => {
    const linked: NoteSearchHit = {
      id: 'linked-note', bookId: 'book-a', bookTitle: 'A Book',
      content: 'A thought about [[Alpha]]', selectedText: 'A passage',
      snippet: 'A passage', topicNames: ['Alpha'], createdAt: '2026-09-20T10:00:00Z',
    };
    const unlinked: NoteSearchHit = {
      id: 'unlinked-note', bookId: 'book-b', bookTitle: 'Another Book',
      content: 'A separate thought', selectedText: null,
      snippet: 'A separate thought', topicNames: [], createdAt: '2026-09-20T11:00:00Z',
    };

    const browse = (items: NoteSearchHit[], totalCount = items.length): void => {
      http.expectOne((request) => request.url === '/api/notes')
        .flush({ items, totalCount, offset: 0, limit: 25 });
      fixture.detectChanges();
    };

    it('restores canonical capture wording without closing the inspector', () => {
      component.setViewMode('notes');
      const captured = { ...unlinked, processingMode: 'clarify', hasRawContent: true };
      browse([captured]);
      component.openNotePanel(captured);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('app-note-capture-details')).not.toBeNull();
      component.captureRestored({
        id: captured.id, bookId: captured.bookId, createdAt: captured.createdAt,
        content: 'Original thought about [[Alpha]]', processingMode: 'verbatim',
      });
      expect(component.panelNote()?.content).toBe('Original thought about [[Alpha]]');
      flushMutationRefresh();
      const canonical = { ...captured, content: 'Original thought about [[Alpha]]',
        topicNames: ['Alpha'], processingMode: 'verbatim' };
      http.expectOne(`/api/notes/${captured.id}`).flush(canonical);
      browse([canonical]);
      expect(component.panelNote()).toEqual(canonical);
      expect(component.browseNotes()[0].processingMode).toBe('verbatim');
      expect(fixture.nativeElement.querySelector('[data-testid="brain-note-inspector"]')).not.toBeNull();
    });

    it('reconciles an old capture restore without overwriting the newly focused note', () => {
      component.setViewMode('notes');
      browse([linked, unlinked]);
      component.openNotePanel(linked);
      component.captureRestored({
        id: unlinked.id, bookId: unlinked.bookId, createdAt: unlinked.createdAt,
        content: 'Restored old note', processingMode: 'verbatim',
      });
      flushMutationRefresh();
      const canonical = { ...unlinked, content: 'Restored old note', processingMode: 'verbatim' };
      http.expectOne(`/api/notes/${unlinked.id}`).flush(canonical);
      browse([linked, canonical]);
      expect(component.panelNote()?.id).toBe(linked.id);
      expect(component.panelNote()?.content).toBe(linked.content);
    });

    it('uses the shared segmented recipe for Notes and Topics navigation', () => {
      fixture.detectChanges();

      const nav = fixture.nativeElement.querySelector('.brain-areas') as HTMLElement;
      const tabs = [...nav.querySelectorAll('button')] as HTMLButtonElement[];

      expect(tabs).toHaveLength(2);
      expect(tabs.map((tab) => tab.textContent?.trim())).toEqual(['Notes', 'Topics']);
      expect(tabs.every((tab) => tab.classList.contains('toggle-opt'))).toBe(true);
      expect(tabs.every((tab) => !tab.classList.contains('nostos-button'))).toBe(true);
    });

    it('keeps the list/map control scoped to Topics while Notes uses the shared toolbar', () => {
      fixture.detectChanges();

      const topicsControl = fixture.nativeElement.querySelector(
        '.brain-header .view-mode-control',
      ) as HTMLElement;
      expect(topicsControl).toBeTruthy();

      component.setViewMode('notes');
      browse([]);

      expect(fixture.nativeElement.querySelector('.brain-header .view-mode-control')).toBeNull();
      const noteSearch = fixture.nativeElement.querySelector(
        '#brain-all-notes-search',
      ) as HTMLInputElement;
      expect(noteSearch).toBeTruthy();
      expect(noteSearch.placeholder).toBe('Search notes, quotes, books…');

      component.setViewMode('list');
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.brain-header .view-mode-control')).toBeTruthy();
    });

    it('opens a bounded all-notes view and finds linked and unlinked material without a search', () => {
      fixture.detectChanges();
      expect(http.match((request) => request.url === '/api/notes')).toEqual([]);
      component.setViewMode('notes');
      browse([linked, unlinked]);

      expect(component.browseTotal()).toBe(2);
      expect(fixture.nativeElement.querySelectorAll('.index-list .note-row-item').length).toBe(2);
      (fixture.nativeElement.querySelectorAll('.index-list .note-row-item')[1] as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.brain-browse-detail')?.textContent).toContain('A separate thought');
      expect(fixture.nativeElement.querySelector('.brain-browse-detail')?.textContent).toContain('No topics linked');
    });

    it('uses one ordered action row and a keyboard-operable More disclosure in Notes', async () => {
      component.setViewMode('notes');
      browse([linked]);

      const header = fixture.nativeElement.querySelector('.brain-header') as HTMLElement;
      const noteSearch = header.querySelector('#brain-all-notes-search') as HTMLInputElement;
      expect(noteSearch).toBeTruthy();
      expect(noteSearch.getAttribute('aria-label')).toBe('Search saved notes');
      expect(fixture.nativeElement.querySelector('.index-col #brain-all-notes-search')).toBeNull();

      const withoutTopics = fixture.nativeElement.querySelector(
        '.brain-note-filter-row button.nostos-chip',
      ) as HTMLButtonElement;
      expect(withoutTopics).toBeTruthy();
      expect(withoutTopics.getAttribute('aria-pressed')).toBe('false');
      expect(fixture.nativeElement.querySelector('app-dropdown.brain-note-order')).toBeTruthy();

      (fixture.nativeElement.querySelector('.index-list .note-row-item') as HTMLButtonElement).click();
      fixture.detectChanges();

      const detail = fixture.nativeElement.querySelector('.brain-browse-detail') as HTMLElement;
      const card = detail.querySelector('app-note-card') as HTMLElement;
      expect(card).toBeTruthy();
      expect(card.querySelector('.note-quote .quote-text')?.textContent).toContain('A passage');
      expect(card.querySelector('.note-text')?.textContent).toContain('A thought about');
      expect(detail.querySelector('.review-quote')).toBeNull();
      expect(detail.textContent).toContain('Linked topics');
      expect(detail.textContent).not.toContain('Confirmed');

      const actions = detail.querySelector('.note-inspector-actions[aria-label="Note actions"]') as HTMLElement;
      const link = [...actions.querySelectorAll('button')].find(
        (button: HTMLButtonElement) => button.textContent?.trim() === 'Link to topic',
      ) as HTMLButtonElement;
      const suggest = actions.querySelector('[data-testid="browse-suggest-topics"]') as HTMLButtonElement;
      expect(link.classList.contains('nostos-button--primary')).toBe(true);
      const keep = [...actions.querySelectorAll('button')].find(
        (button: HTMLButtonElement) => button.textContent?.includes('Keep with writing')
      ) as HTMLButtonElement;
      expect(keep.classList.contains('nostos-button--secondary')).toBe(true);
      expect(suggest.classList.contains('nostos-button--ghost')).toBe(true);
      const row = actions.querySelector('.brain-note-action-row') as HTMLElement;
      const labels = [...row.querySelectorAll('button')].map((button) => button.textContent?.trim());
      expect(labels).toEqual(['Link to topic', 'Keep with writing…', 'Suggest topics Optional', 'More']);
      expect(detail.querySelector('.source-badge')?.textContent).toContain('Open book');

      const more = actions.querySelector('.brain-note-more-trigger') as HTMLButtonElement;
      more.click();
      fixture.detectChanges();
      expect(more.getAttribute('aria-expanded')).toBe('true');
      expect(actions.querySelector('[data-note-more-item]')?.textContent).toContain('Edit note');
      const disclosure = actions.querySelector('.brain-note-more-disclosure') as HTMLElement;
      disclosure.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await Promise.resolve();
      fixture.detectChanges();
      expect(actions.querySelector('.brain-note-more-disclosure')).toBeNull();
      expect(more.getAttribute('aria-expanded')).toBe('false');
      expect(document.activeElement).toBe(more);
    });

    it('filters without topics, then enters focused review and returns to the filtered Notes view', () => {
      component.setViewMode('notes');
      browse([linked, unlinked]);
      component.setBrowseWithoutTopics(true);
      const request = http.expectOne((req) => req.url === '/api/notes');
      expect(request.request.params.get('withoutTopics')).toBe('true');
      request.flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      fixture.detectChanges();

      component.openReview();
      fixture.detectChanges();
      const reviewList = fixture.nativeElement.querySelector('[aria-label="Notes with no topic"]') as HTMLElement;
      expect(reviewList.getAttribute('aria-busy')).toBe('true');
      expect(reviewList.querySelector('nostos-loading-indicator')?.getAttribute('role')).toBe('status');
      http.expectOne((req) => req.url === '/api/notes/unlinked')
        .flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      fixture.detectChanges();
      expect(reviewList.getAttribute('aria-busy')).toBe('false');
      expect(reviewList.querySelector('nostos-loading-indicator')).toBeNull();
      component.closeReview();
      http.expectNone((req) => req.url === '/api/notes');
      fixture.detectChanges();
      expect(component.viewMode()).toBe('notes');
      expect(component.browseWithoutTopics()).toBe(true);
    });

    it('returns to the same selected note and loaded page when review changes nothing', () => {
      component.setViewMode('notes');
      browse([linked, unlinked]);
      component.openNotePanel(unlinked);
      component.setBrowseWithoutTopics(true);
      browse([unlinked]);
      component.openNotePanel(unlinked);

      component.openReview();
      http.expectOne((request) => request.url === '/api/notes/unlinked')
        .flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      component.closeReview();

      expect(component.viewMode()).toBe('notes');
      expect(component.panelNote()?.id).toBe(unlinked.id);
      expect(component.browseNotes().map((note) => note.id)).toEqual([unlinked.id]);
      http.expectNone((request) => request.url === '/api/notes');
    });

    it('opens a topic-search result in the complete Notes inspector', () => {
      component.searchQuery.set('separate');
      component.openNotePanel(unlinked);
      component.openSearchNoteInNotes();

      const request = http.expectOne((req) => req.url === '/api/notes');
      expect(request.request.params.get('query')).toBe('separate');
      request.flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      fixture.detectChanges();
      expect(component.viewMode()).toBe('notes');
      expect(component.panelNote()?.id).toBe(unlinked.id);
      expect(fixture.nativeElement.querySelector('.brain-browse-detail')?.textContent).toContain('Link to topic');
    });

    it('filters one book through the server and reviews only matching unlinked notes', () => {
      component.setViewMode('notes');
      browse([linked, unlinked]);
      component.openNotePanel(unlinked);
      fixture.detectChanges();
      const more = fixture.nativeElement.querySelector('.brain-browse-detail .brain-note-more-trigger') as HTMLButtonElement;
      more.click();
      fixture.detectChanges();
      const sourceFilter = [...fixture.nativeElement.querySelectorAll('.brain-browse-detail button')]
        .find((button: HTMLButtonElement) => button.textContent?.includes('Show notes from this book')) as HTMLButtonElement;
      sourceFilter.click();
      const filtered = http.expectOne((request) => request.url === '/api/notes');
      expect(filtered.request.params.get('bookId')).toBe('book-b');
      filtered.flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.brain-book-filter')?.textContent).toContain('Another Book');

      component.openReview();
      const review = http.expectOne((request) => request.url === '/api/notes');
      expect(review.request.params.get('bookId')).toBe('book-b');
      expect(review.request.params.get('withoutTopics')).toBe('true');
      review.flush({ items: [unlinked], totalCount: 1, offset: 0, limit: 25 });
      component.closeReview();
      http.expectNone((request) => request.url === '/api/notes');
      expect(component.browseBookId()).toBe('book-b');

      component.setBrowseBook(null);
      const cleared = http.expectOne((request) => request.url === '/api/notes');
      expect(cleared.request.params.has('bookId')).toBe(false);
      cleared.flush({ items: [linked, unlinked], totalCount: 2, offset: 0, limit: 25 });
    });

    it('retains an unsaved edit and an unlinked note after a failed save', () => {
      component.setViewMode('notes');
      browse([unlinked]);
      component.openNotePanel(unlinked);
      component.startBrowseEdit();
      component.browseEditContent.set('An edited thought');
      component.closeNotePanel();
      expect(component.panelNote()?.id).toBe(unlinked.id);

      component.saveBrowseEdit();
      http.expectOne((req) => req.method === 'PUT' && req.url === `/api/notes/${unlinked.id}`)
        .error(new ProgressEvent('network-error'));
      expect(component.browseEditing()).toBe(true);
      expect(component.browseNotes().map((note) => note.id)).toEqual([unlinked.id]);
    });

    it('creates a topic through a canonical note link and removes only the filtered row', () => {
      component.setViewMode('notes');
      browse([unlinked]);
      component.setBrowseWithoutTopics(true);
      browse([unlinked]);
      component.openNotePanel(unlinked);
      component.openBrowsePicker();
      component.browsePickerQuery.set('New idea');
      expect(component.browseNewTopicName()).toBe('New idea');

      component.createBrowseTopic();
      const save = http.expectOne((req) => req.method === 'PUT' && req.url === `/api/notes/${unlinked.id}`);
      expect(save.request.body.content).toBe('A separate thought\n\n[[New idea]]');
      save.flush({ ...unlinked, content: save.request.body.content });
      http.expectOne('/api/topics').flush(topics);
      http.expectOne('/api/topics/stats').flush(stats);

      expect(component.browseTotal()).toBe(0);
      expect(component.browseNotes()).toEqual([]);
      expect(component.panelNote()?.topicNames).toEqual(['New idea']);
    });

    it('links an inline proposal through the assistant Act receipt without losing the note inspector', () => {
      component.setViewMode('notes');
      browse([unlinked]);
      component.setBrowseWithoutTopics(true);
      browse([unlinked]);
      component.openNotePanel(unlinked);
      fixture.detectChanges();
      expect(TestBed.inject(AssistantContextService).context().brainReviewNoteId).toBe(unlinked.id);

      (fixture.nativeElement.querySelector('[data-testid="browse-suggest-topics"]') as HTMLButtonElement).click();
      const proposal = http.expectOne('/api/assistant/turn/stream');
      expect(proposal.request.body.context.brainReviewNoteId).toBe(unlinked.id);
      proposal.flush({
        reply: 'A connection.', acknowledgement: null, anchorPrompt: null, pendingPlan: null,
        suggestions: [{ kind: 'topic', label: 'Alpha', reason: 'Both describe this idea.', value: 'c-alpha', noteId: unlinked.id }],
      });
      fixture.detectChanges();
      const buttons = fixture.nativeElement.querySelectorAll('[data-testid="brain-proposal"] button');
      (buttons[1] as HTMLButtonElement).click();
      const link = http.expectOne('/api/assistant/turn/stream');
      expect(link.request.body.message).toContain('c-alpha');
      link.flush({
        reply: 'Linked.', acknowledgement: null, anchorPrompt: null, pendingPlan: null,
        suggestions: [], executedCapabilities: ['notes_link_existing_topic'],
      });
      http.expectOne('/api/topics').flush(topics);
      http.expectOne('/api/topics/stats').flush(stats);
      fixture.detectChanges();

      expect(component.browseNotes()).toEqual([]);
      expect(component.browseTotal()).toBe(0);
      expect(component.panelNote()?.content).toContain('[[Alpha]]');
      expect(component.proposalState()).toBe('idle');
    });
  });

  describe('Brain → Writing source handoff (#492)', () => {
    it('keeps selection controls hidden until Select sources is explicitly entered', () => {
      component.selectTopic('c-alpha');
      flushDetail('c-alpha', detailWithNotes('c-alpha', 'Alpha'));
      fixture.detectChanges();

      expect(component.sourceSelectionMode()).toBe(false);
      expect(fixture.nativeElement.querySelector('.source-select-control')).toBeNull();
      expect(fixture.nativeElement.querySelector('.note-search-box')).toBeNull();
      expect(fixture.nativeElement.querySelector('#source-filter')).toBeNull();
      expect(fixture.nativeElement.querySelector('#note-sort')).toBeNull();
      expect(fixture.nativeElement.querySelector('.note-count')).toBeNull();

      const selectSources = [...fixture.nativeElement.querySelectorAll('button')].find(
        (button: HTMLButtonElement) => button.textContent?.trim() === 'Select sources'
      ) as HTMLButtonElement;
      expect(selectSources).toBeTruthy();

      selectSources.click();
      fixture.detectChanges();

      const checkboxes = [
        ...fixture.nativeElement.querySelectorAll('.source-select-control input'),
      ] as HTMLInputElement[];
      expect(checkboxes.length).toBe(3);
      expect(component.selectedSourceCount()).toBe(0);

      checkboxes[0].checked = true;
      checkboxes[0].dispatchEvent(new Event('change'));
      checkboxes[1].checked = true;
      checkboxes[1].dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(component.selectedSourceCount()).toBe(2);
      expect(component.selectedSourceNoteIds()).toEqual(
        new Set(['c-alpha-newest', 'c-alpha-middle'])
      );
      expect(fixture.nativeElement.textContent).toContain('2 selected');
      expect(fixture.nativeElement.textContent).toContain('Keep with writing…');
    });

    it('routes a focused search-result note through the same note-ID handoff', () => {
      const hit: NoteSearchHit = {
        id: 'search-note-1',
        bookId: 'book-search',
        bookTitle: 'Search source',
        content: 'A focused result',
        selectedText: null,
        snippet: 'A focused result',
        topicNames: ['Alpha'],
        createdAt: '2026-09-20T10:00:00Z',
      };

      component.openNotePanel(hit);
      fixture.detectChanges();

      const panelActions = [...fixture.nativeElement.querySelectorAll('.brain-note-panel-actions button')] as HTMLButtonElement[];
      expect(panelActions.map((button) => button.textContent?.trim())).toEqual([
        'Open in Notes', 'Keep with writing…',
      ]);
      expect(fixture.nativeElement.querySelector('.brain-note-provenance .brain-source-link')?.textContent)
        .toContain('Open book');
      const keep = panelActions[1];
      expect(keep.querySelector('nostos-icon')?.getAttribute('name')).toBe('bookmark-simple');

      keep.click();
      fixture.detectChanges();

      expect(component.handoffNoteIds()).toEqual(['search-note-1']);
      http.expectOne('/api/writings').flush([]);
    });

    it('deduplicates entry IDs before opening the destination picker', () => {
      component.openWritingHandoff(['note-1', 'note-1', 'note-2']);
      expect(component.handoffNoteIds()).toEqual(['note-1', 'note-2']);
    });

    it('retains only failed selections after a partial handoff', () => {
      component.sourceSelectionMode.set(true);
      component.selectedSourceNoteIds.set(new Set(['note-1', 'note-2', 'note-3']));
      component.handoffNoteIds.set(['note-1', 'note-2', 'note-3']);

      component.handleWritingHandoffCompleted({
        succeededNoteIds: ['note-1', 'note-2'],
        failedNoteIds: ['note-3'],
      });

      expect(component.sourceSelectionMode()).toBe(true);
      expect(component.selectedSourceNoteIds()).toEqual(new Set(['note-3']));
      expect(component.handoffNoteIds()).toEqual(['note-3']);
    });

    it('cleans up source selection after successful completion', () => {
      component.sourceSelectionMode.set(true);
      component.selectedSourceNoteIds.set(new Set(['note-1']));
      component.handoffNoteIds.set(['note-1']);

      component.handleWritingHandoffCompleted({
        succeededNoteIds: ['note-1'],
        failedNoteIds: [],
      });

      expect(component.sourceSelectionMode()).toBe(false);
      expect(component.selectedSourceNoteIds().size).toBe(0);
      expect(component.handoffNoteIds()).toEqual([]);
    });

    it('clears source selection when the current topic evidence context is abandoned', () => {
      component.selectTopic('c-alpha');
      flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
      component.sourceSelectionMode.set(true);
      component.selectedSourceNoteIds.set(new Set(['c-alpha-n1']));

      component.selectTopic('c-beta');
      flushDetail('c-beta', detail('c-beta', 'Beta'));

      expect(component.sourceSelectionMode()).toBe(false);
      expect(component.selectedSourceNoteIds().size).toBe(0);
    });
  });

  it('never raises the loading state when a topic is selected from cache', async () => {
    component.selectTopic('c-alpha');
    expect(component.loadingDetail()).toBe(true);
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    expect(component.loadingDetail()).toBe(false);

    // Second visit to the same topic: served from the detail cache, so the
    // pane must never enter — let alone render — a waiting state. No request,
    // and `loadingDetail` never true.
    component.selectTopic('c-beta');
    flushDetail('c-beta', detail('c-beta', 'Beta'));
    await fixture.whenStable();

    component.selectTopic('c-alpha');
    expect(component.loadingDetail()).toBe(false);
    expect(component.selectedDetail()!.name).toBe('Alpha');
    http.expectNone('/api/topics/c-alpha');
  });

  it('prefetches on hover so the click is already a cache hit', async () => {
    component.prefetch('c-gamma');
    component.prefetch('c-gamma');

    const requests = http.match('/api/topics/c-gamma');
    expect(requests).toHaveLength(1);
    requests[0].flush(detail('c-gamma', 'Gamma'));
    await fixture.whenStable();

    component.selectTopic('c-gamma');
    flushRelated('c-gamma');
    expect(http.match('/api/topics/c-gamma')).toHaveLength(0);
    expect(component.loadingDetail()).toBe(false);
    expect(component.selectedDetail()!.name).toBe('Gamma');
  });

  it('ignores a slow response for a topic the user has already left', async () => {
    component.selectTopic('c-alpha');
    const slow = http.expectOne('/api/topics/c-alpha');
    flushRelated('c-alpha');
    component.selectTopic('c-beta');
    flushDetail('c-beta', detail('c-beta', 'Beta'));
    await fixture.whenStable();

    // The abandoned Alpha response lands late and must not hijack the pane.
    slow.flush(detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    expect(component.selectedDetail()!.name).toBe('Beta');
  });

  it('renders no loading surface and no arrival animation on the pane', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    const el: HTMLElement = fixture.nativeElement;
    // The covering wait-field and the `is-waiting` dim class were the visible
    // flash: both are gone from the template.
    expect(el.querySelector('.wait-field')).toBeNull();
    expect(el.querySelector('.is-waiting')).toBeNull();
    expect(el.querySelector('.note-card')).not.toBeNull();
    expect(el.querySelector('.index-tools')).not.toBeNull();
  });

  it('keeps the existing pane visible during a cold switch without a loading surface', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    component.selectTopic('c-gamma');
    fixture.detectChanges();

    expect(component.selectedDetail()?.name).toBe('Alpha');
    expect(fixture.nativeElement.querySelector('.topic-title')?.textContent).toContain('Alpha');
    expect(fixture.nativeElement.querySelector('.content-col .wait-field')).toBeNull();
    expect(fixture.nativeElement.querySelector('.detail-wait-field')).toBeNull();

    flushDetail('c-gamma', detail('c-gamma', 'Gamma'));
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.content-col .wait-field')).toBeNull();
    expect(fixture.nativeElement.querySelector('.topic-title')?.textContent).toContain('Gamma');
  });

  it('filters notes by source and keeps the live count in sync', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detailWithFourNotes('c-alpha', 'Alpha'));
    await fixture.whenStable();
    fixture.detectChanges();

    const sourceTrigger = fixture.nativeElement.querySelector('#source-filter') as HTMLButtonElement;
    const source = sourceTrigger.closest('app-dropdown') as HTMLElement;
    expect(source.textContent).toContain('Ideas in Motion (2)');
    expect(source.textContent).toContain('Meditations (1)');
    expect(component.filteredNotes()).toHaveLength(4);
    expect(fixture.nativeElement.querySelector('.note-count')).toBeNull();

    component.setSourceFilter('Meditations');
    fixture.detectChanges();
    expect(component.filteredNotes().map((note) => note.noteId)).toEqual(['c-alpha-oldest']);
    expect(fixture.nativeElement.querySelector('.note-count')?.textContent).toContain(
      'Showing 1 of 4 notes'
    );
  });

  it('sorts notes by newest, oldest and source order', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detailWithNotes('c-alpha', 'Alpha'));
    await fixture.whenStable();

    const ids = () => component.filteredNotes().map((note) => note.noteId);
    expect(ids()).toEqual(['c-alpha-newest', 'c-alpha-middle', 'c-alpha-oldest']);

    component.setNoteSort('oldest');
    expect(ids()).toEqual(['c-alpha-oldest', 'c-alpha-middle', 'c-alpha-newest']);

    component.setNoteSort('source');
    expect(ids()).toEqual(['c-alpha-middle', 'c-alpha-newest', 'c-alpha-oldest']);
  });

  it('explains related topics through inspectable shared-note evidence before navigating', async () => {
    const alphaDetail = detailWithNotes('c-alpha', 'Alpha');
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', alphaDetail, [
      {
        id: 'c-beta',
        name: 'Beta',
        sharedNotes: 2,
        sharedNoteIds: ['c-alpha-newest', 'c-alpha-oldest'],
      },
    ]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.related-explanation')?.textContent).toContain(
      'same saved notes'
    );
    const inspect = fixture.nativeElement.querySelector(
      '.related-evidence-toggle'
    ) as HTMLButtonElement;
    expect(inspect.textContent).toContain('2 shared notes');

    inspect.click();
    fixture.detectChanges();

    expect(component.relatedEvidenceNotes().map((note) => note.noteId)).toEqual([
      'c-alpha-newest',
      'c-alpha-oldest',
    ]);
    expect(fixture.nativeElement.querySelector('[data-testid="related-evidence"]')?.textContent).toContain(
      'co-occurrence is the reason'
    );
    expect(
      fixture.nativeElement.querySelectorAll('.related-evidence-notes app-note-card')
    ).toHaveLength(2);

    (fixture.nativeElement.querySelector('.related-chip') as HTMLButtonElement).click();
    expect(component.selectedId()).toBe('c-beta');

    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));
    http.expectOne('/api/topics/c-beta/related').flush([]);
    fixture.detectChanges();
    flushChildTopicLists();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.topic-title')?.textContent).toContain('Beta');
  });

  it('puts captured evidence before secondary topic management in the detail DOM', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    fixture.detectChanges();

    const evidence = fixture.nativeElement.querySelector(
      '[data-testid="topic-evidence"]'
    ) as HTMLElement;
    const management = fixture.nativeElement.querySelector('.topic-management') as HTMLElement;

    expect(evidence).toBeTruthy();
    expect(management).toBeTruthy();
    expect(evidence.compareDocumentPosition(management) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(management.querySelector('summary')?.textContent?.trim()).toBe('Manage topic');
  });

  it('optimistically edits a note and keeps the updated detail cached', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    fixture.detectChanges();

    const more = fixture.nativeElement.querySelector('.note-actions .brain-note-more-trigger') as HTMLButtonElement;
    more.click();
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.note-actions .brain-note-more-item') as HTMLButtonElement).click();
    fixture.detectChanges();
    const editor = fixture.nativeElement.querySelector('.note-input') as HTMLTextAreaElement;
    editor.value = 'Updated note about [[Alpha]]';
    editor.dispatchEvent(new Event('input', { bubbles: true }));
    (fixture.nativeElement.querySelector('.edit-actions .icon-btn') as HTMLButtonElement).click();

    expect(component.selectedDetail()!.notes[0].content).toBe('Updated note about [[Alpha]]');
    const update = http.expectOne('/api/notes/c-alpha-n1');
    expect(update.request.method).toBe('PUT');
    update.flush({
      id: 'c-alpha-n1',
      bookId: 'b1',
      content: 'Updated note about [[Alpha]]',
      selectedText: undefined,
      cfiRange: undefined,
      createdAt: '2026-09-12T12:00:00Z',
      bookTitle: 'Meditations',
    });
    http.expectOne('/api/topics/c-alpha').flush({
      ...detail('c-alpha', 'Alpha'),
      notes: [{ ...detail('c-alpha', 'Alpha').notes[0], content: 'Updated note about [[Alpha]]' }],
    });
    http.expectOne('/api/topics/c-alpha/related').flush([]);
    flushMutationRefresh();
    await fixture.whenStable();

    component.selectTopic('c-beta');
    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));
    flushRelated('c-beta');
    await fixture.whenStable();
    component.selectTopic('c-alpha');
    expect(component.selectedDetail()!.notes[0].content).toBe('Updated note about [[Alpha]]');
    http.expectNone('/api/topics/c-alpha');
  });

  it('restores an optimistic note edit when saving fails', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    component.onUpdateNote({ id: 'c-alpha-n1', content: 'Unsaved change', selectedText: '' });
    expect(component.selectedDetail()!.notes[0].content).toBe('Unsaved change');
    http.expectOne('/api/notes/c-alpha-n1').error(new ProgressEvent('network-error'));
    await fixture.whenStable();

    expect(component.selectedDetail()!.notes[0].content).toBe('A note about [[Alpha]]');
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('changes reverted');
  });

  it('confirms deletion before removing the note card', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    fixture.detectChanges();

    component.onDeleteNote('c-alpha-n1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.note-card')).toHaveLength(1);
    expect(http.match('/api/notes/c-alpha-n1')).toHaveLength(0);

    (fixture.nativeElement.querySelector('.btn-confirm') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.note-card')).toHaveLength(0);
    const removal = http.expectOne('/api/notes/c-alpha-n1');
    expect(removal.request.method).toBe('DELETE');
    removal.flush(null);
    http.expectOne('/api/topics/c-alpha/related').flush([]);
    flushMutationRefresh();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  it('refreshes every stale topic count when an edit changes its [[ ]] links', async () => {
    const alphaDetail: TopicDetailDto = {
      ...detail('c-alpha', 'Alpha'),
      notes: [{
        ...detail('c-alpha', 'Alpha').notes[0],
        content: 'Old note about [[Alpha]] and [[Beta]]',
      }],
    };
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', alphaDetail, [{ id: 'c-beta', name: 'Beta', sharedNotes: 1 }]);
    await fixture.whenStable();

    component.onUpdateNote({ id: 'c-alpha-n1', content: 'New note about [[Alpha]]', selectedText: '' });
    expect(component.selectedDetail()?.notes[0].content).toBe('New note about [[Alpha]]');
    const update = http.expectOne('/api/notes/c-alpha-n1');
    update.flush({
      id: 'c-alpha-n1',
      bookId: 'b1',
      content: 'New note about [[Alpha]]',
      selectedText: undefined,
      cfiRange: undefined,
      createdAt: '2026-09-12T12:00:00Z',
      bookTitle: 'Meditations',
    });

    expect(component.loadingDetail()).toBe(true);
    http.expectOne('/api/topics/c-alpha').flush({
      ...alphaDetail,
      notes: [{ ...alphaDetail.notes[0], content: 'New note about [[Alpha]]' }],
    });
    http.expectOne('/api/topics/c-alpha/related').flush([]);
    flushMutationRefresh(
      [
        { id: 'c-alpha', name: 'Alpha', usageCount: 9 },
        { id: 'c-beta', name: 'Beta', usageCount: 2 },
        topics[2],
      ],
      { ...stats, totalReferences: 14 }
    );
    await fixture.whenStable();

    expect(component.loadingDetail()).toBe(false);
    expect(component.selectedDetail()?.notes[0].content).toBe('New note about [[Alpha]]');
    expect(component.relatedTopics()).toEqual([]);
    expect(component.topics().find((topic) => topic.id === 'c-beta')?.usageCount).toBe(2);
    expect(component.topicStats()?.totalReferences).toBe(14);
  });

  it('selects a topic tag rendered inside a note card', async () => {
    component.selectTopic('c-alpha');
    flushDetail(
      'c-alpha',
      {
        ...detail('c-alpha', 'Alpha'),
        notes: [{ ...detail('c-alpha', 'Alpha').notes[0], content: 'See [[Beta]] here' }],
      },
      []
    );
    await fixture.whenStable();
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.topic-tag') as HTMLElement).click();
    expect(component.selectedId()).toBe('c-beta');
    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));
    http.expectOne('/api/topics/c-beta/related').flush([]);
    fixture.detectChanges();
    flushChildTopicLists();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.topic-title')?.textContent).toContain('Beta');
  });

  it('sorts the index by usage, A-Z and Z-A, and persists the choice', () => {
    const names = () => component.filteredTopics().map((c) => c.name);

    expect(names()).toEqual(['Alpha', 'Beta', 'Gamma']);

    component.setSort('az');
    expect(names()).toEqual(['Alpha', 'Beta', 'Gamma']);
    expect(localStorage.getItem('nostos.brain.indexSort')).toBe('az');

    component.setSort('za');
    expect(names()).toEqual(['Gamma', 'Beta', 'Alpha']);

    component.setSort('usage');
    expect(names()).toEqual(['Alpha', 'Beta', 'Gamma']);
  });

  it('filters by name and reports the filtered count', () => {
    component.searchQuery.set('bet');
    expect(component.filteredTopics().map((c) => c.name)).toEqual(['Beta']);
    component.searchQuery.set('nothing-matches-this');
    expect(component.filteredTopics().length).toBe(0);
  });

  it('shows the decorative stats line and ignores a failed stats request', async () => {
    const el: HTMLElement = fixture.nativeElement;
    fixture.detectChanges();
    expect(el.querySelector('.index-stats')?.textContent).toContain('3 topics · 15 references');

    const second = TestBed.createComponent(SecondBrain);
    second.detectChanges();
    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').error(new ProgressEvent('network-error'));

    await second.whenStable();
    expect(second.nativeElement.querySelector('.index-stats')).toBeNull();
  });

  it('keeps the Brain hierarchy calm while search feedback remains accessible', () => {
    fixture.detectChanges();

    const header = fixture.nativeElement.querySelector('.brain-header') as HTMLElement;
    expect(header.querySelector('.badge-count')).toBeNull();
    expect(header.querySelector('.brain-header-purpose')?.textContent?.trim()).toBe(
      'Revisit what you noticed. See what connects.'
    );
    expect(header.querySelector('.index-stats')?.textContent).toContain('3 topics · 15 references');

    const topicHeader = () =>
      Array.from(
        fixture.nativeElement.querySelectorAll('.brain-section-header') as NodeListOf<HTMLElement>
      ).find((section) => section.querySelector('.brain-section-title')?.textContent?.trim() === 'Topics');
    expect(topicHeader()?.querySelector('.brain-section-count')).toBeNull();

    component.searchQuery.set('bet');
    fixture.detectChanges();

    expect(header.querySelector('[role="status"]')?.textContent).toContain(
      'Showing 1 of 3 topics'
    );
    expect(topicHeader()?.querySelector('.brain-section-count')).toBeNull();
  });

  it('normalizes diacritics and ranks exact, prefix and substring matches', () => {
    component.topics.set([
      { id: 'exact', name: 'Théâtre', usageCount: 1 },
      { id: 'prefix-a', name: 'Theatre Alpha', usageCount: 1 },
      { id: 'prefix-b', name: 'Theatre Beta', usageCount: 1 },
      { id: 'substring', name: 'A Theatre', usageCount: 99 },
    ]);
    component.setSort('az');
    component.searchQuery.set('theatre');

    expect(component.filteredTopics().map((topic) => topic.name)).toEqual([
      'Théâtre',
      'Theatre Alpha',
      'Theatre Beta',
      'A Theatre',
    ]);
  });

  it('highlights a diacritic-insensitive match without replacing the source name', () => {
    component.searchQuery.set('theatre');

    expect(component.highlightName('Théâtre')).toEqual([
      { text: 'Théâtre', highlight: true },
    ]);
  });

  it('clears search through the affordance and Escape', () => {
    component.setSearchQuery('bet');
    fixture.detectChanges();

    const clear = fixture.nativeElement.querySelector('.clear-search') as HTMLButtonElement;
    expect(clear).toBeTruthy();
    clear.click();
    expect(component.searchQuery()).toBe('');

    component.setSearchQuery('alp');
    const input = fixture.nativeElement.querySelector('input[aria-label="Search topics and notes"]');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(component.searchQuery()).toBe('');
  });

  it('uses kit primitives for generic Brain chrome while preserving pressed view semantics', () => {
    fixture.detectChanges();

    const search = fixture.nativeElement.querySelector(
      'input[aria-label="Search topics and notes"]',
    ) as HTMLInputElement;
    const sortTrigger = fixture.nativeElement.querySelector('#brain-sort') as HTMLButtonElement;
    const sort = sortTrigger.closest('app-dropdown') as HTMLElement;

    expect(search.classList.contains('nostos-form-control--input')).toBe(true);
    expect(search.classList.contains('nostos-form-control--compact')).toBe(true);
    expect(sort.tagName).toBe('APP-DROPDOWN');
    expect(sort.classList.contains('nostos-dropdown--compact')).toBe(true);
    expect(sortTrigger.getAttribute('role')).toBe('combobox');

    component.setSearchQuery('alp');
    fixture.detectChanges();
    const clear = fixture.nativeElement.querySelector('.clear-search') as HTMLButtonElement;
    expect(clear.classList.contains('icon-btn')).toBe(true);
    expect(clear.getAttribute('aria-label')).toBe('Clear search');
    expect(clear.hasAttribute('aria-pressed')).toBe(false);

    const views = Array.from(
      fixture.nativeElement.querySelectorAll('.view-mode-control .vt-opt'),
    ) as HTMLButtonElement[];
    expect(views.every((view) => view.hasAttribute('aria-pressed'))).toBe(true);
    expect(views.every((view) => !view.classList.contains('nostos-button'))).toBe(true);
  });

  it('moves the roving cursor with arrows and selects on Enter', async () => {
    fixture.detectChanges();
    const rows = fixture.nativeElement.querySelectorAll('.index-item') as NodeListOf<HTMLElement>;
    const scrollSpy = vi.fn();
    rows[1].scrollIntoView = scrollSpy;

    rows[0].focus();
    rows[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(component.cursorIndex()).toBe(1);
    expect(document.activeElement).toBe(rows[1]);
    expect(scrollSpy).toHaveBeenCalledWith({ block: 'nearest' });

    rows[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(component.selectedId()).toBe('c-beta');
    flushRelated('c-beta');
    http.expectOne('/api/topics/c-alpha').flush(detail('c-alpha', 'Alpha'));
    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));
    fixture.detectChanges();
    flushChildTopicLists();
    await fixture.whenStable();
  });

  it('moves from the search input into the first filtered row', () => {
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('input[aria-label="Search topics and notes"]');
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));

    const firstRow = fixture.nativeElement.querySelector('.index-item') as HTMLElement;
    expect(component.cursorIndex()).toBe(0);
    expect(document.activeElement).toBe(firstRow);
    http.expectOne('/api/topics/c-alpha').flush(detail('c-alpha', 'Alpha'));
  });

  it('renders distinct empty states for an empty index and an empty search', async () => {
    component.topics.set([]);
    component.loadingTopics.set(false);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.empty-index-state')?.textContent).toContain(
      'Link a topic'
    );
    expect(fixture.nativeElement.querySelector('a[routerLink="/library"]')).toBeTruthy();

    component.topics.set(topics);
    component.setSearchQuery('xyz');
    fixture.detectChanges();
    // The server search must also come back empty, or the state it renders is a
    // half-answer (issue #158).
    await settleNoteSearch([]);
    expect(fixture.nativeElement.querySelector('.empty-index-state')?.textContent).toContain(
      'No topic names match “xyz”'
    );
    expect(fixture.nativeElement.querySelector('.empty-clear')).toBeTruthy();
  });

  it('reaches note text: a content-only match appears, ranked and labelled', async () => {
    // Issue #158. The index payload carries no note text, so a word that only
    // appears inside a note can only be matched on the server — and it must be
    // labelled, because it is not a topic of that name.
    component.setSearchQuery('sisyphus');
    await settleNoteSearch([
      { id: 'c-gamma', name: 'Absurdity', usageCount: 2, noteMatchCount: 3, noteMatchSnippet: '…Sisyphus, whom the gods…' },
      { id: 'c-alpha', name: 'Alpha', usageCount: 5, noteMatchCount: 1, noteMatchSnippet: '…Sisyphus again…' },
    ]);

    // No name matches at all here, so both rows are content matches, most matches first.
    expect(component.filteredTopics().map((c) => c.name)).toEqual(['Absurdity', 'Alpha']);

    const labels = fixture.nativeElement.querySelectorAll('[data-testid="index-note-match"]');
    expect(labels.length).toBe(2);
    expect(labels[0].textContent).toContain('3 notes');
    expect(labels[0].textContent).toContain('Sisyphus');
    expect(labels[1].textContent).toContain('1 note');
    // Singular vs plural is the sort of thing a test should pin.
    expect(labels[1].textContent).not.toContain('1 notes');
  });

  it('keeps a name match in place and still labels the notes it also matches', async () => {
    component.setSearchQuery('alp');
    await settleNoteSearch([
      { id: 'c-alpha', name: 'Alpha', usageCount: 5, noteMatchCount: 2, noteMatchSnippet: '…alpha…' },
    ]);

    // Name matches lead and keep their ranking; the row gains the label rather
    // than being duplicated as a content match.
    expect(component.filteredTopics().map((c) => c.name)).toEqual(['Alpha']);
    expect(fixture.nativeElement.querySelectorAll('.index-item').length).toBe(1);
    expect(
      fixture.nativeElement.querySelector('[data-testid="index-note-match"]')?.textContent
    ).toContain('2 notes');
  });

  it('leaves the merge picker on name matches, and carries the query into the notes', async () => {
    component.setSearchQuery('sisyphus');
    await settleNoteSearch([
      { id: 'c-gamma', name: 'Absurdity', usageCount: 2, noteMatchCount: 3, noteMatchSnippet: '…' },
    ]);

    // The picker answers "which topic did I mean", so it must never offer a row
    // that only matched by content. It has its own query box, so search that.
    component.mergeSearchQuery.set('sisyphus');
    expect(component.mergeCandidates().map((c) => c.name)).toEqual([]);
    component.mergeSearchQuery.set('');

    (fixture.nativeElement.querySelector('.index-item') as HTMLButtonElement).click();
    http.expectOne('/api/topics/c-gamma').flush(detail('c-gamma', 'Absurdity'));
    flushRelated('c-gamma');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(component.selectedId()).toBe('c-gamma');
    // The content match is only useful if the notes that matched are the ones shown.
    expect(component.noteSearchQuery()).toBe('sisyphus');
  });

  it('uses the shared quiet loading indicator while the first list request is pending', () => {
    const loadingFixture = TestBed.createComponent(SecondBrain);
    loadingFixture.detectChanges();

    const indicator = loadingFixture.nativeElement.querySelector(
      'nostos-loading-indicator.index-loading',
    ) as HTMLElement | null;
    expect(indicator).not.toBeNull();
    expect(indicator?.getAttribute('role')).toBe('status');
    expect(indicator?.textContent).toContain('Loading topics');
    expect(loadingFixture.nativeElement.querySelector('.wait-field')).toBeNull();
    expect(loadingFixture.nativeElement.querySelector('.index-item')).toBeNull();

    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').flush(stats);

  });

  it('shows letter separators for alphabetical order but not usage order', () => {
    component.topics.set([
      { id: 'a', name: 'Aster', usageCount: 1 },
      { id: 'b', name: 'Birch', usageCount: 2 },
      { id: 'c', name: 'Cedar', usageCount: 3 },
    ]);

    component.setSort('az');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.letter-separator').length).toBe(3);

    component.setSort('usage');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.letter-separator').length).toBe(0);
  });

  it('reveals row actions and emits their topic id without selecting the row', () => {
    fixture.detectChanges();
    const rename = fixture.nativeElement.querySelector('.row-action:not(.danger)') as HTMLButtonElement;
    const remove = fixture.nativeElement.querySelector('.row-action.danger') as HTMLButtonElement;
    const renameSpy = vi.fn();
    const deleteSpy = vi.fn();
    component.renameRequested.subscribe(renameSpy);
    component.deleteRequested.subscribe(deleteSpy);

    rename.click();
    remove.click();

    expect(renameSpy).toHaveBeenCalledWith('c-alpha');
    expect(deleteSpy).toHaveBeenCalledWith('c-alpha');
    expect(component.selectedId()).toBeNull();
  });

  it('renders the index as a list of real buttons with a live result count', () => {
    fixture.detectChanges();

    const index = fixture.nativeElement.querySelector('.index-list') as HTMLElement;
    expect(index.getAttribute('role')).toBe('list');
    expect(index.getAttribute('aria-label')).toBe('Topic index');
    expect(index.querySelectorAll('.index-row-shell[role="listitem"]')).toHaveLength(3);
    expect(index.querySelectorAll('.index-item[role="button"]')).toHaveLength(0);
    expect(index.querySelector('.index-row-shell .index-item')?.tagName).toBe('BUTTON');
    expect(fixture.nativeElement.querySelector('.brain-header [role="status"]')?.textContent).toContain(
      'Showing 3 of 3 topics'
    );
  });

  it('gives every icon-only action an accessible name after note cards render', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    fixture.detectChanges();

    const iconOnlyButtons = [...fixture.nativeElement.querySelectorAll('button')].filter((button) => {
      return button.querySelector('nostos-icon') && !button.textContent?.trim();
    }) as HTMLButtonElement[];

    expect(iconOnlyButtons.length).toBeGreaterThan(0);
    expect(iconOnlyButtons.every((button) => button.getAttribute('aria-label')?.trim())).toBe(true);
  });

  it('keeps the document width within a 390px mobile viewport', () => {
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(
      document.documentElement.clientWidth || document.documentElement.scrollWidth
    );
  });

  it('renames inline, sends the topic body, updates the pane, and refreshes stats', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    component.startRename('c-alpha', 'header');
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('.header-rename .inline-rename-input') as HTMLInputElement;
    expect(input.value).toBe('Alpha');

    component.renameValue.set('Nietzsche');
    component.commitRename('c-alpha');
    const rename = http.expectOne('/api/topics/c-alpha');
    expect(rename.request.method).toBe('PUT');
    expect(rename.request.body).toEqual({ topic: 'Nietzsche' });
    rename.flush({ id: 'c-alpha', name: 'Nietzsche', usageCount: 9 });

    http.expectOne('/api/topics/c-alpha/related').flush([]);
    flushMutationRefresh([
      { id: 'c-alpha', name: 'Nietzsche', usageCount: 9 },
      topics[1],
      topics[2],
    ]);
    await fixture.whenStable();

    expect(component.renameId()).toBeNull();
    expect(component.topics().find((topic) => topic.id === 'c-alpha')?.name).toBe('Nietzsche');
    expect(component.selectedDetail()?.name).toBe('Nietzsche');
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe('Renamed to Nietzsche');
  });

  it('rejects an empty inline rename without a request and shows a quiet message', () => {
    component.startRename('c-alpha');
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('.inline-rename-input') as HTMLInputElement;
    expect(input.value).toBe('Alpha');

    component.renameValue.set('   ');
    component.commitRename('c-alpha');
    fixture.detectChanges();

    expect(component.renameError()).toBe('A topic name is required.');
    expect(http.match('/api/topics/c-alpha')).toHaveLength(0);
    expect(component.topics().find((topic) => topic.id === 'c-alpha')?.name).toBe('Alpha');
  });

  it('rolls back a failed rename and reports the failure', async () => {
    component.startRename('c-alpha');
    component.renameValue.set('Unavailable');
    component.commitRename('c-alpha');
    http.expectOne('/api/topics/c-alpha').error(new ProgressEvent('network-error'));
    await fixture.whenStable();

    expect(component.renameId()).toBeNull();
    expect(component.topics().find((topic) => topic.id === 'c-alpha')?.name).toBe('Alpha');
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('changes were not saved');
  });

  it('treats rename onto an existing name as a merge and refreshes the row count', () => {
    component.startRename('c-alpha');
    component.renameValue.set('Beta');
    component.commitRename('c-alpha');

    const rename = http.expectOne('/api/topics/c-alpha');
    expect(rename.request.method).toBe('PUT');
    expect(rename.request.body).toEqual({ topic: 'Beta' });
    rename.flush({ id: 'c-beta', name: 'Beta', usageCount: 4 });
    flushMutationRefresh(
      [
        { id: 'c-beta', name: 'Beta', usageCount: 4 },
        topics[2],
      ],
      { ...stats, totalTopics: 2, totalReferences: 12 }
    );

    expect(component.topics().map((topic) => topic.id)).toEqual(['c-beta', 'c-gamma']);
    expect(component.topics()).toHaveLength(2);
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe('Merged into Beta');
  });

  it('searches merge targets, confirms the consequence, and selects the survivor', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    component.openMergePicker();
    component.mergeSearchQuery.set('bet');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.merge-target')).toHaveLength(1);
    expect(fixture.nativeElement.querySelector('.merge-target')?.textContent).toContain('Beta');

    component.chooseMergeTarget('c-beta');
    component.openMergeConfirmation();
    fixture.detectChanges();
    const modal = fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
    expect(modal.textContent).toContain('move 1 note');
    expect(modal.textContent).toContain('source topic “Alpha” will disappear');

    (modal.querySelector('.btn-confirm') as HTMLButtonElement).click();
    const merge = http.expectOne('/api/topics/c-alpha/merge');
    expect(merge.request.method).toBe('POST');
    expect(merge.request.body).toEqual({ targetId: 'c-beta' });
    expect(component.mergingTopic()).toBe(true);

    merge.flush({ id: 'c-beta', name: 'Beta', usageCount: 4 });
    http.expectOne('/api/topics/c-beta/related').flush([]);
    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));
    flushMutationRefresh(
      [topics[1], topics[2]],
      { ...stats, totalTopics: 2, totalReferences: 14 }
    );
    await fixture.whenStable();

    expect(component.mergingTopic()).toBe(false);
    expect(component.selectedId()).toBe('c-beta');
    expect(component.topics().map((topic) => topic.id)).toEqual(['c-beta', 'c-gamma']);
    expect(component.selectedDetail()?.name).toBe('Beta');
    expect(component.topicStats()?.totalTopics).toBe(2);
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('Merged Alpha into Beta');
  });

  it('rolls back a failed merge and keeps both topics', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();
    component.openMergePicker();
    component.chooseMergeTarget('c-beta');
    component.openMergeConfirmation();
    component.confirmMerge();

    http.expectOne('/api/topics/c-alpha/merge').error(new ProgressEvent('network-error'));
    await fixture.whenStable();
    expect(component.mergingTopic()).toBe(false);
    expect(component.topics().map((topic) => topic.id)).toEqual(['c-alpha', 'c-beta', 'c-gamma']);
    expect(component.selectedId()).toBe('c-alpha');
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('changes were not saved');
  });

  it('confirms topic deletion with honest reference wording and refreshes the stats line', async () => {
    component.selectTopic('c-alpha');
    flushDetail('c-alpha', detail('c-alpha', 'Alpha'));
    await fixture.whenStable();

    component.openDeleteTopic('c-alpha');
    fixture.detectChanges();
    const modal = fixture.nativeElement.querySelector('.confirm-modal-card') as HTMLElement;
    expect(modal.textContent).toContain('does not edit note text');
    expect(modal.textContent).toContain('[[Alpha]] reference stays in notes');
    expect(modal.textContent).toContain('saving a note again will re-create the topic');

    (modal.querySelector('.btn-confirm') as HTMLButtonElement).click();
    const deletion = http.expectOne('/api/topics/c-alpha');
    expect(deletion.request.method).toBe('DELETE');
    expect(component.deletingTopic()).toBe(true);
    deletion.flush(null);
    flushMutationRefresh(
      [topics[1], topics[2]],
      { ...stats, totalTopics: 2, totalReferences: 6 }
    );
    await fixture.whenStable();

    expect(component.deletingTopic()).toBe(false);
    expect(component.selectedId()).toBeNull();
    expect(component.selectedDetail()).toBeNull();
    expect(component.topics().map((topic) => topic.id)).toEqual(['c-beta', 'c-gamma']);
    expect(component.topicStats()?.totalTopics).toBe(2);
    expect(fixture.nativeElement.querySelector('.topic-title')).toBeNull();
  });

  it('rolls back a failed topic deletion and leaves its row intact', async () => {
    component.openDeleteTopic('c-alpha');
    component.confirmDeleteTopic();
    http.expectOne('/api/topics/c-alpha').error(new ProgressEvent('network-error'));
    await fixture.whenStable();

    expect(component.deletingTopic()).toBe(false);
    expect(component.topicDeleteTarget()).toBeNull();
    expect(component.topics().some((topic) => topic.id === 'c-alpha')).toBe(true);
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('still in your index');
  });

  it('restores the persisted sort order on construction', () => {
    localStorage.setItem('nostos.brain.indexSort', 'za');
    const second = TestBed.createComponent(SecondBrain);
    expect(second.componentInstance.indexSort()).toBe('za');
    // Flush the second instance's own list request before it is discarded.
    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').flush(stats);

  });

  it('toggles between list and map views and persists the choice', () => {
    const map = fixture.nativeElement.querySelector('.view-mode-control .vt-opt:last-child') as HTMLButtonElement;
    map.click();
    fixture.detectChanges();

    expect(component.viewMode()).toBe('map');
    expect(localStorage.getItem('nostos.brain.viewMode')).toBe('map');
    expect(fixture.nativeElement.querySelector('app-topic-map')).toBeTruthy();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));

    const list = fixture.nativeElement.querySelector('.view-mode-control .vt-opt:first-child') as HTMLButtonElement;
    list.click();
    fixture.detectChanges();
    expect(component.viewMode()).toBe('list');
    expect(localStorage.getItem('nostos.brain.viewMode')).toBe('list');
    expect(fixture.nativeElement.querySelector('app-topic-map')).toBeNull();
  });

  it('renders the map on the main stage, not in the index rail', () => {
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();

    const map = fixture.nativeElement.querySelector('app-topic-map') as HTMLElement;
    expect(map).toBeTruthy();
    // The graph must live in the content column (the main stage). In the ~320px
    // index rail it was unreadable; this pins the placement so a future change
    // cannot quietly push it back into the sidebar.
    expect(map.closest('.content-col')).not.toBeNull();
    expect(map.closest('.index-col')).toBeNull();
    expect(map.closest('.index-list')).toBeNull();

    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
  });

  it('closes the index rail so the map owns the whole layout in map view', () => {
    fixture.detectChanges();
    const layout = fixture.nativeElement.querySelector('.brain-layout') as HTMLElement;
    const index = fixture.nativeElement.querySelector('.index-col') as HTMLElement;

    // List view: the rail is present and the layout is two columns.
    expect(index.classList.contains('map-hidden')).toBe(false);
    expect(layout.classList.contains('map-view')).toBe(false);
    expect(getComputedStyle(layout).gridTemplateColumns).toContain('280px');

    (fixture.nativeElement.querySelector('.view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();

    // Flush the child's requests FIRST: an assertion failure here would
    // otherwise abort the test before afterEach's http.verify() ran, and the
    // open request would cascade into every later test in the file.
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
    fixture.detectChanges();

    // Map view: the rail is closed and the LAYOUT itself collapses to one
    // column. Hiding only the aside would leave an empty 280-320px grid track
    // and the graph would still fill just the `1fr` beside it — the exact
    // "both views at once" state the mode is supposed to remove.
    expect(index.classList.contains('map-hidden')).toBe(true);
    expect(layout.classList.contains('map-view')).toBe(true);
    expect(getComputedStyle(index).display).toBe('none');
    expect(
      getComputedStyle(layout).gridTemplateColumns,
      'the rail\'s track must be gone, not just the rail'
    ).not.toContain('280px');
  });

  /**
   * The search carries across the mode toggle.
   *
   * It used to be cleared on entering map view, because the search lived in the
   * index rail (which map view closes) and a leftover query would shrink the
   * graph with nothing on screen to explain or clear it. The search now lives in
   * the persistent header, visible in both modes, so the query is a normal
   * persistent filter and the map draws the filtered set like the list does.
   */
  it('carries the index search across the mode toggle', async () => {
    fixture.detectChanges();

    component.setSearchQuery('alp');
    await settleNoteSearch([]);
    expect(component.filteredTopics().map((topic) => topic.name)).toEqual(['Alpha']);

    (fixture.nativeElement.querySelector('.brain-header .view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
    fixture.detectChanges();

    expect(component.searchQuery()).toBe('alp');
    expect(component.filteredTopics().map((topic) => topic.name)).toEqual(['Alpha']);

    // The map draws the filtered set — the same set the list was showing — so
    // the two modes agree about what "the topics" means.
    const map = fixture.debugElement.query(By.css('app-topic-map'));
    expect(map.componentInstance.topics.map((c: { name: string }) => c.name)).toEqual(['Alpha']);
  });

  it('leaves map view from the persistent header, with the rail restored', () => {
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.brain-header .view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
    fixture.detectChanges();

    // The rail closes in map view, but the header does NOT — it is the reason
    // map view is no longer a one-way door, and why the map no longer needs its
    // own duplicate exit control. The switch must still be on screen here.
    const switchInMap = fixture.nativeElement.querySelector(
      '.brain-header .view-mode-control .vt-opt:first-child'
    ) as HTMLButtonElement;
    expect(switchInMap, 'the mode switch survives map view').toBeTruthy();
    switchInMap.click();
    fixture.detectChanges();

    const index = fixture.nativeElement.querySelector('.index-col') as HTMLElement;
    expect(component.viewMode()).toBe('list');
    expect(fixture.nativeElement.querySelector('app-topic-map')).toBeNull();
    expect(index.classList.contains('map-hidden')).toBe(false);
    expect(getComputedStyle(index).display).not.toBe('none');
    expect(getComputedStyle(fixture.nativeElement.querySelector('.brain-layout') as HTMLElement)
      .gridTemplateColumns).toContain('280px');
  });

  it('opens a double-clicked node as the topic detail, and leaves the map', () => {
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
    fixture.detectChanges();

    // Double-clicking a node must land on the topic's notes — the same place
    // an index row click does — not merely re-select it.
    const map = fixture.debugElement.query(By.css('app-topic-map'));
    expect(map.componentInstance.openTopic.observed, 'the parent must bind openTopic').toBe(true);
    map.componentInstance.openTopic.emit('c-beta');
    fixture.detectChanges();
    flushDetail('c-beta', detail('c-beta', 'Beta'));
    fixture.detectChanges();

    expect(component.selectedId()).toBe('c-beta');
    expect(component.viewMode()).toBe('list');
    expect(fixture.nativeElement.querySelector('.topic-title')?.textContent).toContain('Beta');
  });

  it('drops the selection when the map reports empty space was clicked', () => {
    fixture.detectChanges();
    component.selectTopic('c-beta');
    flushDetail('c-beta', detail('c-beta', 'Beta'));
    fixture.detectChanges();
    expect(component.selectedId()).toBe('c-beta');

    // The map is only rendered in map view, which is also where empty-space
    // clicks happen, so enter the mode before wiring the assertion.
    component.setViewMode('map');
    fixture.detectChanges();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));
    fixture.detectChanges();

    // The index rail highlights the selected row, so a deselect has to reach the
    // parent rather than only the canvas.
    const map = fixture.debugElement.query(By.css('app-topic-map'));
    expect(map, 'the map must be rendered in map view').toBeTruthy();
    expect(
      map.componentInstance.selectionCleared.observed,
      'the parent must bind selectionCleared'
    ).toBe(true);

    map.componentInstance.selectionCleared.emit();
    fixture.detectChanges();

    expect(component.selectedId()).toBeNull();
    expect(component.selectedDetail()).toBeNull();
  });

  it('keeps the map visible when a node is selected and offers a way to the notes', () => {
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.view-mode-control .vt-opt:last-child') as HTMLButtonElement).click();
    fixture.detectChanges();
    flushChildTopicLists();
    http.match('/api/topics/graph').forEach((request) => request.flush({ nodes: [], edges: [] }));

    // Selecting a topic from the map must NOT navigate away from the graph —
    // that would hide the map the moment it was used.
    component.onMapTopicSelected('c-beta');
    fixture.detectChanges();
    flushDetail('c-beta', detail('c-beta', 'Beta'));
    fixture.detectChanges();

    expect(component.viewMode()).toBe('map');
    expect(fixture.nativeElement.querySelector('app-topic-map')).toBeTruthy();
    expect(component.selectedTopicName()).toBe('Beta');

    // ...and the map's own action rail is the explicit route to the notes. The
    // action lives INSIDE app-topic-map now, so the child is asked to emit it
    // rather than the parent template owning a separate button.
    const map = fixture.debugElement.query(By.css('app-topic-map'));
    expect(map, 'the map owns the notes action').toBeTruthy();
    expect(map.componentInstance.selectedName).toBe('Beta');

    map.componentInstance.openNotes.emit();
    fixture.detectChanges();
    expect(component.viewMode()).toBe('list');
    expect(fixture.nativeElement.querySelector('app-topic-map')).toBeNull();
  });

  describe('unlinked-notes review (issue #256)', () => {
    const sampleHits: NoteSearchHit[] = [
      {
        id: 'hit-1',
        bookId: 'b-sisyphus',
        bookTitle: 'The Myth of Sisyphus',
        content: 'One must imagine Sisyphus happy.',
        selectedText: 'The struggle itself toward the heights is enough to fill a man’s heart.',
        snippet: 'One must imagine Sisyphus happy.',
        topicNames: ['Absurdism', 'Revolt'],
        createdAt: '2026-09-01T10:00:00Z',
      },
      {
        id: 'hit-2',
        bookId: 'b-rebel',
        bookTitle: 'The Rebel',
        content: 'I rebel — therefore we exist.',
        selectedText: null,
        snippet: null,
        topicNames: [],
        createdAt: '2026-09-02T10:00:00Z',
      },
    ];

    const thirdHit: NoteSearchHit = {
      id: 'hit-3',
      bookId: 'b-ideas',
      bookTitle: 'Ideas in Motion',
      content: 'A third unconnected remark.',
      selectedText: null,
      snippet: null,
      topicNames: [],
      createdAt: '2026-09-03T10:00:00Z',
    };

    const page = (items: NoteSearchHit[], totalCount = items.length) => ({
      items,
      totalCount,
      offset: 0,
      limit: 25,
    });

    /** Enters review directly for mode-specific tests and answers the first page. */
    const enterReview = (items: NoteSearchHit[] = sampleHits, totalCount = items.length): void => {
      component.openReview();
      fixture.detectChanges();
      http.expectOne((req) => req.url === '/api/notes/unlinked').flush(page(items, totalCount));
      fixture.detectChanges();
    };

    /** A topic link write re-reads the index and the stats line. */
    const settleReviewRefresh = (): void => {
      fixture.detectChanges();
      http.match('/api/topics').forEach((request) => {
        if (!request.cancelled) request.flush(topics);
      });
      http.match('/api/topics/stats').forEach((request) => {
        if (!request.cancelled) request.flush(stats);
      });
      fixture.detectChanges();
    };

    it('shows the topic index alone in the rail, with no notes section beneath it', () => {
      const sectionTitles = fixture.nativeElement.querySelectorAll('.brain-section-title');

      expect(sectionTitles.length).toBe(1);
      expect(sectionTitles[0].textContent?.trim()).toBe('Topics');
      expect(fixture.nativeElement.querySelector('.brain-notes-section')).toBeNull();
      expect(component.noteSearchHits()).toEqual([]);
    });

    it('does not load unlinked notes merely because the Brain was opened', () => {
      const second = TestBed.createComponent(SecondBrain);
      second.detectChanges();
      http.expectOne('/api/topics').flush(topics);
      http.expectOne('/api/topics/stats').flush(stats);

      // The whole point of the issue: nothing here fetched them, so the mode has
      // to fetch them itself when it is opened.
      http.expectNone((req) => req.url === '/api/notes/unlinked');
      expect(second.componentInstance.reviewQueue()).toEqual([]);
      expect(second.componentInstance.reviewLoaded()).toBe(false);
    });

    it('shows note-text matches only while a search is active', async () => {
      component.setSearchQuery('sisyphus');
      await settleNoteSearch([], [sampleHits[0]]);
      fixture.detectChanges();

      const sectionTitles = fixture.nativeElement.querySelectorAll('.brain-section-title');
      expect(sectionTitles.length).toBe(2);
      expect(sectionTitles[1].textContent?.trim()).toBe('Notes');
      expect(component.noteSearchHits()).toEqual([sampleHits[0]]);

      component.clearSearch();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.brain-notes-section')).toBeNull();
      expect(component.noteSearchHits()).toEqual([]);
    });

    it('labels review mode and keeps it out of the Topics and All notes rails', () => {
      expect(fixture.nativeElement.querySelector('.review-entry')).toBeNull();
      expect(fixture.nativeElement.querySelector('.rail-foot-action')).toBeNull();
      component.setViewMode('notes');
      http.expectOne((req) => req.url === '/api/notes')
        .flush(page(sampleHits, sampleHits.length));
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.review-entry')).toBeNull();

      component.setBrowseWithoutTopics(true);
      http.expectOne((req) => req.url === '/api/notes' && req.params.get('withoutTopics') === 'true')
        .flush(page(sampleHits, sampleHits.length));
      fixture.detectChanges();
      const entry = fixture.nativeElement.querySelector('.review-entry') as HTMLElement;
      expect(entry.querySelector('button')?.textContent?.trim()).toBe('Review one by one');
      expect(entry.textContent).toContain('Step through these notes in order. Optional');
      expect(fixture.nativeElement.querySelectorAll('.review-entry button')).toHaveLength(1);

      (entry.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      http.expectOne((req) => req.url === '/api/notes/unlinked').flush(page(sampleHits));
      fixture.detectChanges();

      expect(component.viewMode()).toBe('unlinked');
      expect(fixture.nativeElement.querySelector('.brain-section-title')?.textContent?.trim()).toBe(
        'Reviewing one by one'
      );
      expect(fixture.nativeElement.querySelector('.review-position')?.textContent?.trim()).toBe('1 of 2');
      expect(fixture.nativeElement.querySelector('.review-section-header .review-return')?.textContent?.trim())
        .toBe('Back to notes without topics');
      expect(fixture.nativeElement.querySelectorAll('.index-list .note-row-item').length).toBe(2);
      expect(localStorage.getItem('nostos.brain.viewMode')).toBe('notes');
    });

    it('reuses the ordinary NoteCard presentation in optional review', () => {
      enterReview();

      expect(fixture.nativeElement.querySelector('.note-inspector-title')?.textContent?.trim()).toBe(
        'The Myth of Sisyphus'
      );
      const card = fixture.nativeElement.querySelector('.review-pane app-note-card') as HTMLElement;
      expect(card).toBeTruthy();
      expect(card.querySelector('.note-quote .quote-text')?.textContent).toContain(
        'The struggle itself toward the heights is enough to fill a man’s heart.'
      );
      expect(card.querySelector('.note-text')?.textContent?.trim()).toBe(
        'One must imagine Sisyphus happy.'
      );
      expect(fixture.nativeElement.querySelector('.review-pane .review-quote')).toBeNull();
      expect(fixture.nativeElement.querySelector('.review-pane')?.textContent).toContain('Link to topic');
      const more = fixture.nativeElement.querySelector('.review-pane .brain-note-more-trigger') as HTMLButtonElement;
      more.click();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.review-pane .brain-note-more-item')?.textContent)
        .toContain('Edit note');
    });

    it('offers the same single-note handoff from the focused unlinked-review note', () => {
      enterReview();

      const keep = [...fixture.nativeElement.querySelectorAll('.note-inspector-actions button')].find(
        (button: HTMLButtonElement) => button.textContent?.includes('Keep with writing')
      ) as HTMLButtonElement;
      expect(keep).toBeTruthy();

      keep.click();
      fixture.detectChanges();

      expect(component.handoffNoteIds()).toEqual(['hit-1']);
      http.expectOne('/api/writings').flush([]);
      http.expectNone((request) => request.method === 'PUT' && request.url.startsWith('/api/notes/'));
    });

    it('focuses a queue row without deciding anything about it', () => {
      enterReview();
      (fixture.nativeElement.querySelectorAll('.index-list .note-row-item')[1] as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(component.reviewNote()!.id).toBe('hit-2');
      const rows = fixture.nativeElement.querySelectorAll('.index-list .note-row-item');
      expect(rows[0].classList).not.toContain('active');
      expect(rows[1].classList).toContain('active');
      expect(component.reviewQueue().length).toBe(2);
    });

    it('traverses past the first page instead of silently ending at it', () => {
      enterReview([sampleHits[0]], 3);

      const loadMore = fixture.nativeElement.querySelector('.review-load-more') as HTMLButtonElement;
      expect(loadMore).toBeTruthy();
      expect(loadMore.textContent).toContain('2 not shown');

      loadMore.click();
      fixture.detectChanges();

      const request = http.expectOne((req) => req.url === '/api/notes/unlinked');
      // The offset is the rows still held, so a queue that shrank is still walked
      // from the right place.
      expect(request.request.params.get('offset')).toBe('1');
      request.flush({ items: [sampleHits[1], thirdHit], totalCount: 3, offset: 1, limit: 25 });
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('.index-list .note-row-item').length).toBe(3);
      expect(fixture.nativeElement.querySelector('.review-load-more')).toBeNull();
      expect(component.reviewQueue().length).toBe(component.reviewTotal());
    });

    it('loads the next page when advancing from its last visible note, and moves back without mutating', () => {
      enterReview([sampleHits[0]], 3);
      component.skipReviewNote();
      const next = http.expectOne((request) => request.url === '/api/notes/unlinked');
      expect(next.request.params.get('offset')).toBe('1');
      next.flush({ items: [sampleHits[1], thirdHit], totalCount: 3, offset: 1, limit: 25 });
      fixture.detectChanges();
      expect(component.reviewNote()?.id).toBe(sampleHits[1].id);
      component.previousReviewNote();
      expect(component.reviewNote()?.id).toBe(sampleHits[0].id);
      http.expectNone((request) => request.method === 'PUT' || request.method === 'POST' || request.method === 'DELETE');
    });

    it('links the reviewed note to an existing topic with an explicit reference, and drops it', () => {
      enterReview();
      component.openReviewPicker();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.merge-picker')).toBeTruthy();

      component.chooseReviewTopic('c-alpha');
      fixture.detectChanges();
      (
        fixture.nativeElement.querySelector('.merge-picker-actions .merge-picker-confirm') as HTMLButtonElement
      ).click();
      fixture.detectChanges();

      const put = http.expectOne((req) => req.method === 'PUT' && req.url === '/api/notes/hit-1');
      // The association written is the canonical one: an explicit [[reference]] in
      // the note body, which the server rebuilds the note's topics from.
      expect((put.request.body as { content: string }).content).toBe(
        'One must imagine Sisyphus happy.\n\n[[Alpha]]'
      );
      put.flush({});
      settleReviewRefresh();

      expect(fixture.nativeElement.querySelector('.merge-picker')).toBeNull();
      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-2']);
      expect(component.reviewTotal()).toBe(1);
      expect(fixture.nativeElement.querySelector('.review-position')?.textContent?.trim()).toBe(
        '1 of 1'
      );
      // Resolving moves the review on rather than emptying the pane.
      expect(component.viewMode()).toBe('unlinked');
      expect(component.reviewNote()!.id).toBe('hit-2');
    });

    it('does not write anything when a link fails', () => {
      enterReview();
      component.openReviewPicker();
      component.chooseReviewTopic('c-alpha');
      fixture.detectChanges();

      component.confirmLinkToTopic();
      fixture.detectChanges();
      http
        .expectOne((req) => req.method === 'PUT' && req.url === '/api/notes/hit-1')
        .error(new ProgressEvent('network-error'));
      fixture.detectChanges();

      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-1', 'hit-2']);
      expect(component.reviewTotal()).toBe(2);
      expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('Failed to link');
    });

    it('creates a topic from review through the canonical note update', () => {
      enterReview();
      component.openReviewPicker();
      component.reviewPickerQuery.set('New connection');
      fixture.detectChanges();
      expect(component.reviewNewTopicName()).toBe('New connection');

      component.createReviewTopic();
      const save = http.expectOne((request) => request.method === 'PUT' && request.url === '/api/notes/hit-1');
      expect(save.request.body.content).toContain('[[New connection]]');
      expect(save.request.body.selectedText).toBe(sampleHits[0].selectedText);
      save.flush({});
      settleReviewRefresh();

      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-2']);
      expect(component.reviewTotal()).toBe(1);
    });

    it('offers retry rather than an empty-queue claim when the review fetch fails', () => {
      component.openReview();
      http.expectOne((request) => request.url === '/api/notes/unlinked')
        .error(new ProgressEvent('network-error'));
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.index-list [role="alert"]')?.textContent)
        .toContain('Notes could not be loaded');
      expect(fixture.nativeElement.querySelector('.index-list')?.textContent).not.toContain('No notes here');
    });

    it('resolves a note whose edit declares a topic', () => {
      enterReview();
      component.startReviewEdit();
      fixture.detectChanges();

      component.reviewEditContent.set('One must imagine Sisyphus happy, about [[Alpha]].');
      component.saveReviewEdit();
      fixture.detectChanges();

      http
        .expectOne((req) => req.method === 'PUT' && req.url === '/api/notes/hit-1')
        .flush({});
      settleReviewRefresh();

      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-2']);
      expect(component.reviewTotal()).toBe(1);
      expect(component.reviewEditing()).toBe(false);
    });

    it('keeps a note queued when an edit still leaves it with no topic', () => {
      enterReview();
      component.startReviewEdit();
      fixture.detectChanges();

      component.reviewEditContent.set('Rewritten, and still connected to nothing.');
      component.saveReviewEdit();
      fixture.detectChanges();

      http.expectOne((req) => req.method === 'PUT' && req.url === '/api/notes/hit-1').flush({});
      settleReviewRefresh();

      // The edit is real but the note is still unlinked, so the queue must not
      // pretend anything was resolved.
      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-1', 'hit-2']);
      expect(component.reviewTotal()).toBe(2);
      expect(component.reviewQueue()[0].content).toBe('Rewritten, and still connected to nothing.');
      expect(component.reviewEditing()).toBe(false);
      expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toBe('Note saved');
    });

    it('skips without mutating the note', () => {
      enterReview();
      const first = component.reviewNote()!.id;

      component.skipReviewNote();
      fixture.detectChanges();

      expect(component.reviewNote()!.id).not.toBe(first);
      expect(component.reviewQueue().length).toBe(2);
      expect(component.reviewTotal()).toBe(2);
      http.expectNone((req) => req.method === 'PUT' || req.method === 'POST' || req.method === 'DELETE');
    });

    it('shows a calm completion state when nothing is waiting', () => {
      enterReview([], 0);

      expect(fixture.nativeElement.textContent).toContain('No notes here');
      expect(fixture.nativeElement.textContent).toContain('You can still browse all your saved notes.');
      expect(fixture.nativeElement.querySelector('.index-list .note-row-item')).toBeNull();
      expect(fixture.nativeElement.querySelector('.review-pane')).toBeNull();
    });

    it('keeps the queue when the user leaves, and never persists the mode', () => {
      enterReview();
      component.closeReview();
      fixture.detectChanges();

      expect(component.viewMode()).toBe('list');
      expect(fixture.nativeElement.querySelector('.brain-section-title')?.textContent?.trim()).toBe(
        'Topics'
      );
      expect(fixture.nativeElement.querySelectorAll('.index-list .note-row-item').length).toBe(0);
      expect(component.reviewQueue().length).toBe(2);
      expect(localStorage.getItem('nostos.brain.viewMode')).toBe('list');
    });

    it('clears a search on entering review, because the box is hidden there', async () => {
      component.setSearchQuery('sisyphus');
      await settleNoteSearch([], [sampleHits[0]]);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('.search-box')).toBeTruthy();

      component.openReview();
      fixture.detectChanges();
      http.expectOne((req) => req.url === '/api/notes/unlinked').flush(page(sampleHits));
      fixture.detectChanges();

      expect(component.searchQuery()).toBe('');
      expect(fixture.nativeElement.querySelector('.search-box')).toBeNull();
      expect(fixture.nativeElement.querySelector('.brain-notes-section')).toBeNull();
    });

    it('shows each queued note with its source, falling back to the note text', () => {
      enterReview();

      const rows = fixture.nativeElement.querySelectorAll('.index-list .note-row-item');
      expect(rows.length).toBe(2);

      expect(rows[0].querySelector('.note-row-snippet')?.textContent?.trim()).toBe('“One must imagine Sisyphus happy.”');
      expect(rows[0].querySelector('.note-row-book')?.textContent?.trim()).toBe('The Myth of Sisyphus');

      // hit-2 has null snippet, so it falls back to content
      expect(rows[1].querySelector('.note-row-snippet')?.textContent?.trim()).toBe('“I rebel — therefore we exist.”');
      expect(rows[1].querySelector('.note-row-book')?.textContent?.trim()).toBe('The Rebel');
    });

    it('still opens the note panel from a search hit', async () => {
      component.setSearchQuery('sisyphus');
      await settleNoteSearch([], sampleHits);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="brain-note-panel"]')).toBeNull();

      const rows = fixture.nativeElement.querySelectorAll('.brain-notes-list .note-row-item');
      expect(rows.length).toBe(2);
      (rows[0] as HTMLButtonElement).click();
      fixture.detectChanges();

      const panel = fixture.nativeElement.querySelector('[data-testid="brain-note-panel"]');
      expect(panel).toBeTruthy();
      expect(panel.getAttribute('role')).toBe('dialog');
      expect(panel.querySelector('.brain-note-panel-title')?.textContent?.trim()).toBe('The Myth of Sisyphus');
      expect(panel.querySelector('blockquote')?.textContent?.trim()).toBe('The struggle itself toward the heights is enough to fill a man’s heart.');
      expect(panel.querySelector('.brain-note-panel-content')?.textContent?.trim()).toBe('One must imagine Sisyphus happy.');

      const topicBadges = panel.querySelectorAll('.nostos-badge--success');
      expect(topicBadges.length).toBe(2);
      expect(topicBadges[0].textContent?.trim()).toContain('Absurdism');
      expect(topicBadges[1].textContent?.trim()).toContain('Revolt');

      const closeBtn = panel.querySelector('button[aria-label="Close note"]') as HTMLButtonElement;
      expect(closeBtn).toBeTruthy();
      closeBtn.click();
      fixture.detectChanges();

      expect(component.panelNote()).toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="brain-note-panel"]')).toBeNull();
    });

    it('shows the unlinked state in the panel when note has no topic links', async () => {
      component.setSearchQuery('rebel');
      await settleNoteSearch([], sampleHits);
      fixture.detectChanges();

      const rows = fixture.nativeElement.querySelectorAll('.brain-notes-list .note-row-item');
      (rows[1] as HTMLButtonElement).click();
      fixture.detectChanges();

      const panel = fixture.nativeElement.querySelector('[data-testid="brain-note-panel"]');
      expect(panel).toBeTruthy();
      expect(panel.querySelector('blockquote')).toBeNull();
      expect(panel.textContent).toContain('No topics linked');
    });

    it('never falls back to unlinked notes when the search box is empty', async () => {
      component.setSearchQuery('sisyphus');
      await settleNoteSearch([], [sampleHits[0]]);
      fixture.detectChanges();

      const searchHeader = fixture.nativeElement.querySelectorAll('.brain-section-title')[1];
      expect(searchHeader.textContent?.trim()).toBe('Notes');
      expect(component.noteSearchHits()).toEqual([sampleHits[0]]);

      const rows = fixture.nativeElement.querySelectorAll('.brain-notes-list .note-row-item');
      expect(rows.length).toBe(1);
      expect(rows[0].querySelector('.note-row-snippet')?.textContent?.trim()).toBe('“One must imagine Sisyphus happy.”');

      // Clearing the query returns the rail to topics only. This is the
      // regression the issue is about: the old section fell back to the whole
      // unlinked list whenever the box was empty.
      component.clearSearch();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.brain-notes-section')).toBeNull();
      expect(component.noteSearchHits()).toEqual([]);
    });

    it('exposes the reviewed note to the assistant context and clears it on leave', () => {
      const assistantContext = TestBed.inject(AssistantContextService);

      enterReview();
      expect(assistantContext.context().brainReviewNoteId).toBe('hit-1');
      expect(assistantContext.context().bookId).toBe('b-sisyphus');

      // Focusing the next note moves the context with it.
      component.focusReviewNote('hit-2');
      fixture.detectChanges();
      expect(assistantContext.context().brainReviewNoteId).toBe('hit-2');

      component.closeReview();
      fixture.detectChanges();
      expect(assistantContext.context().brainReviewNoteId).toBeNull();
    });

    it('shows validated suggestions beside the reviewed note and lets the user inspect evidence', () => {
      enterReview();

      const button = fixture.nativeElement.querySelector(
        '[data-testid="review-suggest-topics"]',
      ) as HTMLButtonElement;
      expect(button).toBeTruthy();

      button.click();
      fixture.detectChanges();

      const assistant = TestBed.inject(AssistantService);
      expect(assistant.isOpen()).toBe(false);
      expect(component.proposalState()).toBe('loading');

      const request = http.expectOne('/api/assistant/turn/stream');
      expect(request.request.body.message).toContain('explicitly propose only grounded links');
      expect(request.request.body.context.brainReviewNoteId).toBe('hit-1');
      request.flush({
        reply: 'Mountains looks right.',
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [
          { kind: 'topic', label: 'Mountains', reason: 'The note describes the same climb.', value: 'c-alpha', noteId: 'hit-1' },
        ],
        pendingPlan: null,
      });
      fixture.detectChanges();

      expect(component.proposalState()).toBe('ready');
      const proposal = fixture.nativeElement.querySelector('[data-testid="brain-proposal"]');
      expect(proposal.textContent).toContain('Suggested topic');
      expect(proposal.textContent).toContain('Mountains');
      expect(proposal.textContent).toContain('same climb');
      const proposalButtons = proposal.querySelectorAll('button');
      expect((proposalButtons[1] as HTMLButtonElement).classList.contains('nostos-button--primary')).toBe(true);

      (proposalButtons[0] as HTMLButtonElement).click();
      http.expectOne('/api/topics/c-alpha').flush({
        id: 'c-alpha', name: 'Mountains', notes: [{ noteId: 'evidence-1', bookId: 'b-1', bookTitle: 'Other book', content: 'A related climb.' }],
      });
      fixture.detectChanges();
      expect(proposal.textContent).toContain('Existing evidence');
      expect(proposal.textContent).toContain('A related climb.');

      (proposal.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(proposal.textContent).not.toContain('A related climb.');
    });

    it('drops late proposals when review focus changes, without linking or showing old chips', () => {
      enterReview();
      component.askNostos();
      const request = http.expectOne('/api/assistant/turn/stream');
      const turnId = request.request.body.turnId as string;
      component.focusReviewNote('hit-2');
      fixture.detectChanges();
      request.flush({
        reply: 'A connection.', acknowledgement: null, anchorPrompt: null, pendingPlan: null,
        suggestions: [{ kind: 'topic', label: 'Alpha', reason: 'Related note.', value: 'c-alpha', noteId: 'hit-1' }],
      });
      fixture.detectChanges();
      expect(component.proposalState()).toBe('idle');
      expect(fixture.nativeElement.querySelector('[data-testid="brain-proposal"]')).toBeNull();
      expect(TestBed.inject(AssistantService).entries()
        .filter((entry) => entry.turnId === turnId).some((entry) => entry.suggestions?.length)).toBe(false);
    });

    it('shows an honest empty suggestion result and leaves manual linking available', () => {
      enterReview();
      component.askNostos();
      http.expectOne('/api/assistant/turn/stream').flush({
        reply: 'No fit.', acknowledgement: null, anchorPrompt: null, pendingPlan: null, suggestions: [],
      });
      fixture.detectChanges();
      expect(component.proposalState()).toBe('empty');
      expect(fixture.nativeElement.querySelector('[data-testid="brain-proposals"]')?.textContent).toContain('No useful matches found');
      expect(fixture.nativeElement.querySelector('.brain-note-action-row')?.textContent).toContain('Link to topic');
    });

    it('keeps manual linking usable when AI is unavailable, and ignores cancelled late results', () => {
      enterReview();
      component.askNostos();
      http.expectOne('/api/assistant/turn/stream').flush({
        reply: '', acknowledgement: null, anchorPrompt: null, pendingPlan: null, suggestions: [],
        error: { code: 'assistant_unavailable', message: 'Ask Nostos is unavailable.' },
      });
      fixture.detectChanges();
      expect(component.proposalState()).toBe('unavailable');
      expect(fixture.nativeElement.querySelector('.brain-note-action-row')?.textContent).toContain('Link to topic');
      expect(fixture.nativeElement.querySelector('[data-testid="brain-proposals"]')?.textContent)
        .toContain('Manual linking');

      component.askNostos();
      const second = http.expectOne('/api/assistant/turn/stream');
      component.cancelTopicProposals();
      second.flush({
        reply: 'A match.', acknowledgement: null, anchorPrompt: null, pendingPlan: null,
        suggestions: [{ kind: 'topic', label: 'Alpha', reason: 'Same theme.', value: 'c-alpha', noteId: 'hit-1' }],
      });
      fixture.detectChanges();
      expect(component.proposalState()).toBe('idle');
      const turnId = second.request.body.turnId as string;
      expect(TestBed.inject(AssistantService).entries()
        .filter((entry) => entry.turnId === turnId).some((entry) => entry.suggestions?.length)).toBe(false);
    });

    it('invalidates visible proposals when the note text changes', () => {
      enterReview();
      component.askNostos();
      http.expectOne('/api/assistant/turn/stream').flush({
        reply: 'A match.', acknowledgement: null, anchorPrompt: null, pendingPlan: null,
        suggestions: [{ kind: 'topic', label: 'Alpha', reason: 'Same theme.', value: 'c-alpha', noteId: 'hit-1' }],
      });
      expect(component.proposalState()).toBe('ready');

      component.reviewQueue.update((rows) => rows.map((row) => row.id === 'hit-1'
        ? { ...row, content: 'The note has been edited.' } : row));
      fixture.detectChanges();
      expect(component.proposalState()).toBe('idle');
      http.expectNone('/api/assistant/turn/stream');
    });

    it('moves the reviewed note out of the queue after an immediate assistant link succeeds', () => {
      enterReview();

      const assistant = TestBed.inject(AssistantService);
      assistant.applySuggestion({
        kind: 'topic',
        label: 'Alpha',
        reason: 'Existing topic in your library.',
        value: 'c-alpha',
        noteId: 'hit-1',
      });

      const turn = http.expectOne('/api/assistant/turn/stream');
      expect(turn.request.body.context.brainReviewNoteId).toBe('hit-1');
      turn.flush({
        reply: 'Linked the note to Alpha.',
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [],
        pendingPlan: null,
        executedCapabilities: ['notes_link_existing_topic'],
      });

      // The receipt describes a real write, so the index/stats refresh and the
      // exact note from that turn leaves the queue.
      settleReviewRefresh();

      expect(component.reviewQueue().map((row) => row.id)).toEqual(['hit-2']);
      expect(component.reviewTotal()).toBe(1);
    });
  });
});

describe('SecondBrain topic-link routing', () => {
  it('opens the requested note evidence from the noteId query parameter', async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'second-brain', component: SecondBrain }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/second-brain?noteId=deep-note', SecondBrain);
    const http = TestBed.inject(HttpTestingController);

    await new Promise((resolve) => setTimeout(resolve, 0));

    const deepNote: NoteSearchHit = {
      id: 'deep-note',
      bookId: 'book-deep',
      bookTitle: 'Deep Book',
      content: 'The exact canonical note.',
      selectedText: 'The exact quotation.',
      snippet: 'The exact quotation.',
      topicNames: ['Attention'],
      createdAt: '2026-09-27T12:00:00Z',
    };

    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').flush(stats);
    http.match((request) => request.url === '/api/notes').forEach((request) => request.flush({
      items: [deepNote],
      totalCount: 1,
      offset: 0,
      limit: 25,
    }));
    http.expectOne('/api/notes/deep-note').flush(deepNote);

    const routed = await navigation;
    harness.detectChanges();

    expect(routed.viewMode()).toBe('notes');
    expect(routed.panelNote()?.id).toBe('deep-note');
    expect(harness.routeNativeElement?.querySelector('.brain-browse-detail')?.textContent)
      .toContain('The exact canonical note.');

    http.verify();
  });

  it('opens the requested topic evidence from the topicId query parameter', async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'second-brain', component: SecondBrain }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/second-brain?topicId=c-beta', SecondBrain);
    const http = TestBed.inject(HttpTestingController);

    // The routed component is created before navigation can settle because its
    // initial HTTP reads are intentionally still outstanding.
    await new Promise((resolve) => setTimeout(resolve, 0));

    http.expectOne('/api/topics').flush(topics);
    http.expectOne('/api/topics/stats').flush(stats);
    http.expectOne('/api/topics/c-beta/related').flush([]);
    http.expectOne('/api/topics/c-beta').flush(detail('c-beta', 'Beta'));

    const routed = await navigation;
    harness.detectChanges();

    expect(routed.selectedId()).toBe('c-beta');
    expect(routed.selectedDetail()?.name).toBe('Beta');
    expect(harness.routeNativeElement?.querySelector('.topic-title')?.textContent).toContain('Beta');
    expect(
      harness.routeNativeElement?.querySelector('[data-testid="topic-evidence"]')
    ).toBeTruthy();

    http.verify();
  });
});
