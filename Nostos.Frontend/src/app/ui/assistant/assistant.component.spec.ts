import { ComponentFixture, TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';

import { AssistantComponent, capabilityDoors } from './assistant.component';
import {
  ASSISTANT_PENDING_DELAY_MS,
  ASSISTANT_SESSION_STORAGE_KEY,
  ASSISTANT_TURN_WATCHDOG_MS,
  AssistantChangesetDto,
  AssistantEntry,
  AssistantEvidenceReferenceDto,
  AssistantService,
  AssistantSourceReferenceDto,
  AssistantTurnArtifact,
  AssistantTurnResponse,
} from './assistant.service';
import { AssistantStatusService } from './assistant-status.service';
import { AssistantAllowanceService } from './assistant-allowance.service';
import type { CloudManagedAiUsage } from '../../core/dtos/cloud-ai-refill.dtos';
import { ASSISTANT_LAUNCHER_ATTR, AssistantLauncherService } from './assistant-launcher.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import {
  AssistantContext,
  AssistantContextService,
} from './assistant-context.service';
import {
  AssistantVoiceError,
  AssistantVoiceService,
  AssistantVoiceStatus,
} from './assistant-voice.service';

/** A full context with everything unset, so each test states only what it means. */
function context(overrides: Partial<AssistantContext> = {}): AssistantContext {
  return {
    surface: 'reader',
    route: '/read/b1',
    bookId: 'b1',
    bookTitle: 'The Magic Mountain',
    bookFormat: null,
    readerType: null,
    epubCfi: null,
    pdfPage: null,
    audioTimestamp: null,
    audioChapter: null,
    selectedText: null,
    readingTarget: 'b1',
    brainReviewNoteId: null,
    topic: null,
    collectionId: null,
    anchor: null,
    ...overrides,
  };
}

/** A context service driven directly by the test, without a Router or books fetch. */
function fakeContextService(initial: Partial<AssistantContext>) {
  const state = signal<AssistantContext>(context(initial));
  return {
    context: state.asReadonly(),
    set: (overrides: Partial<AssistantContext>) => state.set(context(overrides)),
  };
}

/** A voice service the component can drive, with no microphone behind it. */
function fakeVoiceService() {
  const status = signal<AssistantVoiceStatus>('idle');
  const error = signal<AssistantVoiceError | null>(null);
  const elapsedSeconds = signal(0);
  return {
    status: status.asReadonly(),
    error: error.asReadonly(),
    elapsedSeconds: elapsedSeconds.asReadonly(),
    isRecording: computed(() => status() === 'recording'),
    isTranscribing: computed(() => status() === 'transcribing'),
    isBusy: computed(() => status() !== 'idle'),
    onTranscript: null as ((text: string) => void) | null,
    start: vi.fn(),
    stop: vi.fn(),
    cancel: vi.fn(),
    setStatus: (value: AssistantVoiceStatus) => status.set(value),
    setError: (value: AssistantVoiceError | null) => error.set(value),
    setElapsed: (value: number) => elapsedSeconds.set(value),
  };
}

/** The managed allowance, driven directly so no Cloud endpoint is called. */
function fakeAllowanceService() {
  const canConfigure = signal(true);
  const accountUrl = signal<string | null>(null);
  const usage = signal<CloudManagedAiUsage | null>(null);
  return {
    canConfigureAiProvider: canConfigure.asReadonly(),
    accountManagementUrl: accountUrl.asReadonly(),
    usage: usage.asReadonly(),
    usageAvailable: computed(() => true),
    usageFailed: signal(false).asReadonly(),
    ensureCapabilities: vi.fn(),
    ensureUsage: vi.fn(),
    setCanConfigure: (value: boolean) => canConfigure.set(value),
    setAccountUrl: (value: string | null) => accountUrl.set(value),
    setUsage: (value: CloudManagedAiUsage | null) => usage.set(value),
  };
}

/** A status service the component can drive, with no HTTP behind it. */
function fakeStatusService(initial: boolean) {
  const available = signal(initial);
  return {
    available: available.asReadonly(),
    ensureLoaded: vi.fn(),
    refresh: vi.fn(),
    setAvailable: (value: boolean) => available.set(value),
  };
}

/** A minimal successful turn: a short reply and nothing else. */
function turn(overrides: Partial<AssistantTurnResponse> = {}): AssistantTurnResponse {
  return {
    reply: 'Noted.',
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: [],
    changeset: null,
    capturedNoteId: null,
    ...overrides,
  };
}

function reviewChangeset(overrides: Partial<AssistantChangesetDto> = {}): AssistantChangesetDto {
  return {
    changesetId: 'cs-9',
    status: 'review',
    steps: [
      { index: 0, summary: 'Delete collection “Fiction”', included: true, dependsOn: [] },
      { index: 1, summary: 'Create collection “Science fiction”', included: true, dependsOn: [] },
      { index: 2, summary: 'Add “Dune” to “Science fiction”', included: true, dependsOn: [1] },
    ],
    diff: {
      collections: [
        { id: 'c1', kind: 'deleted', name: 'Fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 2 },
        { id: 'c2', kind: 'created', name: 'Science fiction', previousName: null, parentName: null, previousParentName: null, parentChanged: false, bookCount: 1 },
      ],
      memberships: [
        { bookId: 'b1', title: 'Dune', author: 'Frank Herbert', added: ['Science fiction'], removed: ['Fiction'] },
      ],
      bookEdits: [],
      counts: {
        collectionsCreated: 2, collectionsDeleted: 3, collectionsUpdated: 0, booksTouched: 5,
        membershipsAdded: 5, membershipsRemoved: 5, booksLosingMembership: 5, bookEdits: 0,
      },
    },
    diffHash: 'hash-9',
    actionToken: 'tok-9',
    reviewReasons: ['Deletes 3 collections that hold 5 books between them.'],
    canUndo: false,
    ...overrides,
  };
}

describe('capabilityDoors', () => {
  const ctx = (overrides: Partial<AssistantContext>): AssistantContext =>
    ({ surface: 'other', route: '/', bookId: null, bookTitle: null, selectedText: null, ...overrides }) as AssistantContext;

  it('groups every example under Ask, Capture and Organise, two or three each', () => {
    for (const surface of ['reader', 'book-detail', 'brain', 'studio', 'library'] as const) {
      const doors = capabilityDoors(ctx({ surface, bookTitle: 'Dune' }));
      expect(doors.map((door) => door.label)).toEqual(['Ask', 'Capture', 'Organise']);
      for (const door of doors) {
        expect(door.examples.length, `${surface}/${door.id}`).toBeGreaterThanOrEqual(2);
        expect(door.examples.length, `${surface}/${door.id}`).toBeLessThanOrEqual(3);
        expect(new Set(door.examples.map((example) => example.id)).size).toBe(door.examples.length);
      }
    }
  });

  it('names quotes, collections, the To read list and Studio drafts, which the app omitted', () => {
    const text = capabilityDoors(ctx({ surface: 'library' }))
      .flatMap((door) => door.examples.map((example) => example.fill ?? example.label))
      .join(' ');

    expect(text).toContain('Save this thought:');
    expect(text).toContain('Save this quote:');
    expect(text).toContain('collection');
    expect(text).toContain('To read');
    expect(text).toContain('Studio draft');
  });

  it('asks about the open book in the reader, and about a selection when there is one', () => {
    expect(capabilityDoors(ctx({ surface: 'reader', bookTitle: 'Dune' }))[0].examples.map((p) => p.label)).toEqual([
      'Summarise what I have read of Dune so far',
      'What have I noted from Dune?',
    ]);
    expect(
      capabilityDoors(ctx({ surface: 'reader', bookTitle: 'Dune', selectedText: 'Fear is the mind-killer' }))[0]
        .examples[0].label,
    ).toBe('Explain the passage I selected');
  });

  it('still reads naturally without a book title', () => {
    expect(capabilityDoors(ctx({ surface: 'book-detail' }))[0].examples[0].label).toBe(
      'What is this book about?',
    );
  });

  it('suggests topics instead of a Studio draft when a Brain note is under review', () => {
    const doors = capabilityDoors(ctx({ surface: 'brain', brainReviewNoteId: 'note-1' }));

    expect(doors[2].examples.map((example) => example.label)).toContain('Suggest topics for this note');
    expect(doors[2].examples.map((example) => example.label)).not.toContain(
      'Open my Studio draft about memory',
    );
  });

  it('falls back to library questions anywhere else', () => {
    for (const surface of ['library', 'settings', 'home', 'other'] as const) {
      expect(capabilityDoors(ctx({ surface }))[0].examples[0].id).toBe('library-next');
    }
  });
});

describe('AssistantComponent (Cmd/Ctrl+J)', () => {
  let fixture: ComponentFixture<AssistantComponent>;
  let assistant: AssistantService;
  let http: HttpTestingController;
  let fake: ReturnType<typeof fakeContextService>;
  let voice: ReturnType<typeof fakeVoiceService>;
  let status: ReturnType<typeof fakeStatusService>;
  let allowance: ReturnType<typeof fakeAllowanceService>;
  let router: { navigate: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    localStorage.clear();
    sessionStorage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
    await configureComponent();
  });

  /** Builds the component; called again after resetTestingModule to model a reload. */
  async function configureComponent(): Promise<void> {
    fake = fakeContextService({ surface: 'reader', route: '/read/b1', bookId: 'b1' });
    voice = fakeVoiceService();
    status = fakeStatusService(true);
    allowance = fakeAllowanceService();
    router = { navigate: vi.fn(() => Promise.resolve(true)) };

    await TestBed.configureTestingModule({
      imports: [AssistantComponent],
      providers: [
        { provide: AssistantContextService, useValue: fake },
        { provide: AssistantVoiceService, useValue: voice },
        { provide: AssistantStatusService, useValue: status },
        { provide: AssistantAllowanceService, useValue: allowance },
        { provide: Router, useValue: router },
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AssistantComponent);
    assistant = TestBed.inject(AssistantService);
    http = TestBed.inject(HttpTestingController);

    // Most component tests exercise the enabled surface explicitly. The product
    // default is off; opt in here so those tests remain about assistant behavior.
    TestBed.inject(LibraryPreferencesService).setAssistantEnabled(true);
    fixture.detectChanges();
  }

  afterEach(() => {
    http.verify();
  });

  it('opens a grounded PDF source on /read with physical page and logical label kept separate', () => {
    const source: AssistantSourceReferenceDto = {
      bookId: 'book-pdf',
      bookTitle: 'Grounded PDF',
      bookAuthor: 'Author',
      format: 'pdf',
      sourceSha256: 'a'.repeat(64),
      excerpt: 'Grounded evidence.',
      locators: [
        {
          type: 'pdf',
          pdfPageIndex: 8,
          pdfPageLabel: '7',
        },
      ],
    };

    fixture.componentInstance.openSource(source);

    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'book-pdf'],
      {
        queryParams: {
          sourcePage: 9,
          sourcePageLabel: '7',
        },
      },
    );
  });

  it('opens a grounded EPUB source on /read with CFI and structural fallback data', () => {
    const source: AssistantSourceReferenceDto = {
      bookId: 'book-epub',
      bookTitle: 'Grounded EPUB',
      bookAuthor: null,
      format: 'epub',
      sourceSha256: 'b'.repeat(64),
      excerpt: 'A uniquely grounded passage from the imported EPUB.',
      locators: [
        {
          type: 'epub',
          epubCfi: 'epubcfi(/6/4!/4/2/6:0)',
          epubResourceHref: 'chapter-2.xhtml',
          epubSpineIndex: 2,
          startTextOffset: 314,
        },
      ],
    };

    fixture.componentInstance.openSource(source);

    expect(router.navigate).toHaveBeenCalledWith(
      ['/read', 'book-epub'],
      {
        queryParams: {
          sourceCfi: 'epubcfi(/6/4!/4/2/6:0)',
          sourceHref: 'chapter-2.xhtml',
          sourceSpine: 2,
          sourceOffset: 314,
          sourceExcerpt: source.excerpt,
        },
      },
    );
  });

  it('opens note and topic evidence on canonical Brain deep-links', () => {
    const note: AssistantEvidenceReferenceDto = {
      handle: { kind: 'note', noteId: 'note-42' },
      label: 'Note · The Magic Mountain',
      excerpt: 'A canonical note.',
    };
    const topic: AssistantEvidenceReferenceDto = {
      handle: { kind: 'topic', topicId: 'topic-7' },
      label: 'Attention',
      excerpt: 'Canonical topic evidence.',
    };

    expect(fixture.componentInstance.canOpenEvidence(note)).toBe(true);
    fixture.componentInstance.openEvidence(note);
    expect(router.navigate).toHaveBeenCalledWith(['/brain/notes', 'note-42']);

    expect(fixture.componentInstance.canOpenEvidence(topic)).toBe(true);
    fixture.componentInstance.openEvidence(topic);
    expect(router.navigate).toHaveBeenCalledWith(['/brain/topics', 'topic-7']);
  });

  describe('grounded source grouping (issue #634)', () => {
    function entry(
      overrides: Partial<AssistantEntry> = {},
    ): AssistantEntry {
      return {
        id: 'entry-grounded',
        turnId: 'turn-grounded',
        kind: 'assistant',
        text: 'A grounded answer.',
        anchorLabel: null,
        meta: null,
        sources: [],
        suggestions: [],
        artifacts: [],
        ...overrides,
      };
    }

    function epubEvidence(
      ordinal: number,
      cfi: string,
      excerpt = `Distinct East of Eden passage ${ordinal}.`,
    ): AssistantEvidenceReferenceDto {
      return {
        handle: {
          kind: 'book_text',
          bookId: 'east-of-eden',
          sourceSha256: 'east-sha',
          extractorVersion: 'epub-extractor-v2',
          ordinal,
        },
        label: 'East of Eden',
        bookTitle: 'East of Eden',
        bookAuthor: 'John Steinbeck',
        format: 'epub',
        excerpt,
        locators: [
          {
            type: 'epub',
            epubCfi: cfi,
            epubResourceHref: `chapter-${ordinal}.xhtml`,
            epubSpineIndex: ordinal,
            startTextOffset: ordinal * 100,
          },
        ],
      };
    }

    function pdfEvidence(
      ordinal: number,
      page: number,
    ): AssistantEvidenceReferenceDto {
      return {
        handle: {
          kind: 'book_text',
          bookId: 'pdf-book',
          sourceSha256: 'pdf-sha',
          extractorVersion: 'pdf-extractor-v1',
          ordinal,
        },
        label: 'Grounded PDF',
        bookTitle: 'Grounded PDF',
        bookAuthor: null,
        format: 'pdf',
        excerpt: `Evidence from page ${page}.`,
        locators: [
          {
            type: 'pdf',
            pdfPageIndex: page - 1,
            pdfPageLabel: String(page),
          },
        ],
      };
    }

    function evidenceArtifacts(
      ...evidence: AssistantEvidenceReferenceDto[]
    ): AssistantTurnArtifact[] {
      return evidence.map((item) => ({ kind: 'evidence', evidence: item }));
    }

    function legacySource(
      ordinal: number,
      overrides: Partial<AssistantSourceReferenceDto> = {},
    ): AssistantSourceReferenceDto {
      return {
        bookId: 'east-of-eden',
        bookTitle: 'East of Eden',
        bookAuthor: 'John Steinbeck',
        format: 'epub',
        sourceSha256: 'east-sha',
        excerpt: `Legacy passage ${ordinal}.`,
        locators: [
          {
            type: 'epub',
            epubCfi: `epubcfi(/6/${ordinal * 2}!/4/2:0)`,
            epubResourceHref: `legacy-${ordinal}.xhtml`,
            epubSpineIndex: ordinal,
            startTextOffset: ordinal * 80,
          },
        ],
        ...overrides,
      };
    }

    function renderEntry(value: AssistantEntry): void {
      assistant.open();
      assistant.updateDraft('Render grounded sources.');
      assistant.submit();
      http.expectOne('/api/assistant/turn/stream').flush(turn({
        reply: value.text,
        sources: value.sources ?? [],
        evidence: (value.artifacts ?? []).flatMap((artifact) =>
          artifact.kind === 'evidence' ? [artifact.evidence] : []),
      }));
      fixture.detectChanges();
    }

    function sourceGroups(): NodeListOf<HTMLButtonElement> {
      return fixture.nativeElement.querySelectorAll(
        '[data-testid="assistant-source-group"]',
      );
    }

    function sourceItems(): NodeListOf<HTMLElement> {
      return fixture.nativeElement.querySelectorAll(
        '[data-testid="assistant-source-item"]',
      );
    }

    it('groups three canonical EPUB passages and preserves exact navigation when expanded', () => {
      const first = epubEvidence(1, 'epubcfi(/6/2!/4/2:0)');
      const second = epubEvidence(2, 'epubcfi(/6/4!/4/2:0)');
      const third = epubEvidence(3, 'epubcfi(/6/6!/4/2:0)');

      renderEntry(entry({
        artifacts: evidenceArtifacts(first, second, third),
      }));

      expect(sourceGroups().length).toBe(1);
      expect(sourceGroups()[0].textContent).toContain('East of Eden · 3 passages');
      expect(sourceGroups()[0].getAttribute('aria-expanded')).toBe('false');
      expect(sourceItems().length).toBe(0);

      sourceGroups()[0].click();
      fixture.detectChanges();

      expect(sourceGroups()[0].getAttribute('aria-expanded')).toBe('true');
      expect(sourceItems().length).toBe(3);

      (sourceItems()[1] as HTMLButtonElement).click();

      expect(router.navigate).toHaveBeenCalledWith(
        ['/read', 'east-of-eden'],
        {
          queryParams: expect.objectContaining({
            sourceCfi: 'epubcfi(/6/4!/4/2:0)',
          }),
        },
      );
    });

    describe('numbered citations (issue #1002)', () => {
      function markers(): HTMLAnchorElement[] {
        return Array.from(fixture.nativeElement.querySelectorAll('[data-testid="assistant-entry-markdown"] a.assistant-cite-link'));
      }

      it('renders each [^n] as a numbered marker that previews and opens evidence n', () => {
        const pdf = pdfEvidence(1, 12);
        const epub = epubEvidence(4, 'epubcfi(/6/8!/4/2:0)');
        renderEntry(entry({
          text: 'The page argues one thing[^1], the chapter another[^2].',
          artifacts: evidenceArtifacts(pdf, epub),
        }));

        const links = markers();
        expect(links.map((link) => link.textContent)).toEqual(['1', '2']);
        expect(links[0].closest('sup')).not.toBeNull();
        expect(links[0].getAttribute('title')).toContain('Grounded PDF · p. 12');
        expect(links[0].getAttribute('title')).toContain('Evidence from page 12.');
        expect(fixture.nativeElement.textContent).not.toContain('[^1]');

        links[1].click();
        expect(router.navigate).toHaveBeenCalledWith(
          ['/read', 'east-of-eden'],
          { queryParams: expect.objectContaining({ sourceCfi: 'epubcfi(/6/8!/4/2:0)' }) },
        );
      });

      it('numbers the sources row to match, and only when the reply cites', () => {
        renderEntry(entry({
          text: 'Cited once[^2].',
          artifacts: evidenceArtifacts(pdfEvidence(1, 3), epubEvidence(1, 'epubcfi(/6/2!/4/2:0)')),
        }));
        const numbers = Array.from(sourceItems()).map((item) => item.querySelector('.entry-source-number')?.textContent?.trim());
        expect(numbers).toEqual(['1', '2']);
      });

      it('leaves a reply without markers exactly as before', () => {
        renderEntry(entry({ text: 'No citations here.', artifacts: evidenceArtifacts(pdfEvidence(1, 3)) }));
        expect(markers()).toEqual([]);
        expect(fixture.nativeElement.querySelector('.entry-source-number')).toBeNull();
      });

      it('keeps a marker inside code as code', () => {
        renderEntry(entry({ text: 'Write `[^1]` for a footnote.', artifacts: evidenceArtifacts(pdfEvidence(1, 3)) }));
        expect(markers()).toEqual([]);
        expect(fixture.nativeElement.querySelector('[data-testid="assistant-entry-markdown"] code')?.textContent).toBe('[^1]');
      });
    });

    it('prefers canonical evidence over legacy sources when both are present', () => {
      const evidence = [
        epubEvidence(1, 'epubcfi(/6/2!/4/2:0)', 'Canonical passage one.'),
        epubEvidence(2, 'epubcfi(/6/4!/4/2:0)', 'Canonical passage two.'),
      ];

      renderEntry(entry({
        artifacts: evidenceArtifacts(...evidence),
        sources: [
          legacySource(1, { excerpt: 'LEGACY ONLY one.' }),
          legacySource(2, { excerpt: 'LEGACY ONLY two.' }),
          legacySource(3, { excerpt: 'LEGACY ONLY three.' }),
        ],
      }));

      expect(sourceGroups().length).toBe(1);
      sourceGroups()[0].click();
      fixture.detectChanges();

      expect(sourceItems().length).toBe(2);
      expect(
        fixture.nativeElement.querySelector('[data-testid="assistant-entry-sources"]').textContent,
      ).not.toContain('LEGACY ONLY');
    });

    it('groups legacy sources when an entry has no evidence artifacts', () => {
      renderEntry(entry({
        artifacts: [],
        sources: [
          legacySource(1),
          legacySource(2),
          legacySource(3),
        ],
      }));

      expect(sourceGroups().length).toBe(1);
      expect(sourceGroups()[0].textContent).toContain('East of Eden · 3 passages');
      expect(sourceItems().length).toBe(0);

      sourceGroups()[0].click();
      fixture.detectChanges();

      expect(sourceItems().length).toBe(3);
    });

    it('deduplicates identical canonical handles into one single source pill', () => {
      const duplicate = epubEvidence(7, 'epubcfi(/6/14!/4/2:0)');

      renderEntry(entry({
        artifacts: evidenceArtifacts(
          duplicate,
          {
            ...duplicate,
            excerpt: 'Later duplicate display data must not create another passage.',
            locators: [
              {
                type: 'epub',
                epubCfi: 'epubcfi(/6/999!/4/2:0)',
              },
            ],
          },
        ),
      }));

      expect(sourceGroups().length).toBe(0);
      expect(sourceItems().length).toBe(1);
      expect(sourceItems()[0].textContent).toContain('East of Eden · reading position');
    });

    it('groups PDF evidence while preserving useful page labels for each passage', () => {
      renderEntry(entry({
        artifacts: evidenceArtifacts(
          pdfEvidence(1, 7),
          pdfEvidence(2, 9),
        ),
      }));

      expect(sourceGroups().length).toBe(1);
      expect(sourceGroups()[0].textContent).toContain('Grounded PDF · 2 passages');

      sourceGroups()[0].click();
      fixture.detectChanges();

      expect(sourceItems().length).toBe(2);
      expect(sourceItems()[0].textContent).toContain('Grounded PDF · p. 7');
      expect(sourceItems()[1].textContent).toContain('Grounded PDF · p. 9');
    });

    it('keeps note evidence as an individual pill using the canonical Brain deep-link', () => {
      const note: AssistantEvidenceReferenceDto = {
        handle: {
          kind: 'note',
          noteId: 'note-grounded',
        },
        label: 'A note about attention',
        excerpt: 'Canonical note evidence.',
      };

      renderEntry(entry({
        artifacts: evidenceArtifacts(note),
      }));

      expect(sourceGroups().length).toBe(0);
      expect(sourceItems().length).toBe(1);
      expect(sourceItems()[0].textContent).toContain('A note about attention');

      (sourceItems()[0] as HTMLButtonElement).click();

      expect(router.navigate).toHaveBeenCalledWith(['/brain/notes', 'note-grounded']);
    });
  });

  it('renders the collapsed capsule trigger and toggles on click', () => {
    const trigger = fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]');
    expect(trigger).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeNull();

    trigger.click();
    fixture.detectChanges();

    expect(assistant.isOpen()).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeTruthy();
  });

  it('starts a deliberate new conversation from the header action', () => {
    fixture.componentInstance.open();
    assistant.updateDraft('Keep this in the old conversation');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'Old reply' }));
    fixture.detectChanges();

    const oldConversationId = assistant.conversationId();
    expect(assistant.entries().length).toBe(2);

    const newConversation = fixture.nativeElement.querySelector(
      '[data-testid="assistant-new-conversation"]',
    ) as HTMLButtonElement;
    expect(newConversation).toBeTruthy();
    expect(newConversation.getAttribute('aria-label')).toBe('New conversation');
    expect(newConversation.classList.contains('assistant-new-conversation')).toBe(true);
    expect(newConversation.classList.contains('assistant-header-action')).toBe(true);
    expect(newConversation.classList.contains('icon-btn')).toBe(true);
    // assistant-expand is intentionally hidden by the <=768px media query.
    // The New conversation action must never inherit that desktop-only class.
    expect(newConversation.classList.contains('assistant-expand')).toBe(false);

    newConversation.click();
    fixture.detectChanges();

    expect(assistant.conversationId()).not.toBe(oldConversationId);
    expect(assistant.entries()).toEqual([]);
    expect(assistant.history()).toEqual([]);
  });

  it('expands and collapses the desktop shell without replacing conversation state', () => {
    fixture.componentInstance.open();
    assistant.updateDraft('Keep this draft while the shell changes');
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('[data-testid="assistant-panel"]') as HTMLElement;
    const scrim = fixture.nativeElement.querySelector('.assistant-scrim') as HTMLElement;
    const expand = fixture.nativeElement.querySelector('[data-testid="assistant-expand"]') as HTMLButtonElement;

    expect(fixture.componentInstance.expanded()).toBe(false);
    expect(panel.classList.contains('is-expanded')).toBe(false);
    expect(expand.getAttribute('aria-label')).toBe('Expand Ask Nostos');

    expand.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.expanded()).toBe(true);
    expect(panel.classList.contains('is-expanded')).toBe(true);
    expect(scrim.classList.contains('is-expanded')).toBe(true);
    expect(expand.getAttribute('aria-label')).toBe('Collapse Ask Nostos');
    expect(assistant.draft()).toBe('Keep this draft while the shell changes');
    expect(
      (fixture.nativeElement.querySelector('[data-testid="assistant-composer"]') as HTMLTextAreaElement)
        .value,
    ).toBe('Keep this draft while the shell changes');

    expand.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.expanded()).toBe(false);
    expect(panel.classList.contains('is-expanded')).toBe(false);
    expect(assistant.draft()).toBe('Keep this draft while the shell changes');
  });

  it('keeps the header quiet and uses one action treatment for all three controls', () => {
    fixture.componentInstance.open();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.assistant-subtitle')).toBeNull();

    const actions = [
      fixture.nativeElement.querySelector('[data-testid="assistant-new-conversation"]'),
      fixture.nativeElement.querySelector('[data-testid="assistant-expand"]'),
      fixture.nativeElement.querySelector('[data-testid="assistant-close"]'),
    ] as HTMLButtonElement[];

    expect(actions.every((action) => action.classList.contains('icon-btn'))).toBe(true);
    expect(actions.every((action) => action.classList.contains('assistant-header-action'))).toBe(true);
  });

  it('returns to compact mode after the expanded assistant is closed', () => {
    fixture.componentInstance.open();
    fixture.componentInstance.toggleExpanded();
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(true);

    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(false);

    fixture.componentInstance.open();
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(false);
  });

  it('renders a quiet platform shortcut without a nested keycap', () => {
    const shortcut = fixture.nativeElement.querySelector('.trigger-shortcut') as HTMLElement | null;

    expect(shortcut).toBeTruthy();
    expect(shortcut?.textContent?.trim()).toMatch(/^(⌘ J|Ctrl J)$/);
    expect(fixture.nativeElement.querySelector('.trigger-capsule kbd')).toBeNull();
  });

  it('opens on Cmd/Ctrl+J and prevents the browser default', () => {
    const event = new KeyboardEvent('keydown', { key: 'j', metaKey: true, cancelable: true });
    document.dispatchEvent(event);
    fixture.detectChanges();

    expect(event.defaultPrevented).toBe(true);
    expect(assistant.isOpen()).toBe(true);
  });

  it('does not touch Cmd/Ctrl+K, which the command palette owns', () => {
    const event = new KeyboardEvent('keydown', { key: 'k', metaKey: true, cancelable: true });
    document.dispatchEvent(event);
    fixture.detectChanges();

    expect(event.defaultPrevented).toBe(false);
    expect(assistant.isOpen()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeNull();
  });

  it('hides the capsule and panel when the user preference is off', () => {
    TestBed.inject(LibraryPreferencesService).setAssistantEnabled(false);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeNull();

    // The keyboard shortcut is not hijacked for a feature that is off.
    const event = new KeyboardEvent('keydown', { key: 'j', metaKey: true, cancelable: true });
    document.dispatchEvent(event);
    fixture.detectChanges();
    expect(event.defaultPrevented).toBe(false);
    expect(assistant.isOpen()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeNull();
  });

  it('hides the capsule when the server reports the assistant unavailable', () => {
    status.setAvailable(false);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();

    fixture.componentInstance.open();
    fixture.detectChanges();
    expect(assistant.isOpen()).toBe(false);
  });

  it('shows the capsule when the preference is on and the server is available', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeTruthy();
    expect(status.ensureLoaded).toHaveBeenCalled();
  });

  it('rests the capsule while a hold is active and brings it back on release (#1345)', () => {
    const launcher = TestBed.inject(AssistantLauncherService);
    const trigger = () => fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]');
    expect(trigger().classList).not.toContain('is-resting');

    const release = launcher.hideFloating();
    fixture.detectChanges();
    expect(trigger().classList).toContain('is-resting');

    // The shortcut path still opens the panel while the capsule rests.
    fixture.componentInstance.open();
    fixture.detectChanges();
    expect(assistant.isOpen()).toBe(true);
    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(trigger().classList).toContain('is-resting');

    release();
    fixture.detectChanges();
    expect(trigger().classList).not.toContain('is-resting');
  });

  describe('with the dock carrying the launcher', () => {
    let detach: () => void;
    let launcher: AssistantLauncherService;

    beforeEach(() => {
      launcher = TestBed.inject(AssistantLauncherService);
      detach = launcher.attachDock();
      fixture.detectChanges();
    });

    afterEach(() => detach());

    it('stands its own capsule down and opens the panel above the dock', () => {
      expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();

      launcher.toggle();
      fixture.detectChanges();

      const panel = fixture.nativeElement.querySelector('[data-testid="assistant-panel"]') as HTMLElement;
      expect(panel).toBeTruthy();
      expect(panel.classList).toContain('is-docked');

      launcher.toggle();
      fixture.detectChanges();
      expect(assistant.isOpen()).toBe(false);
    });

    it('does not treat a press on the launcher as an outside click', () => {
      const dockLauncher = document.createElement('button');
      dockLauncher.setAttribute(ASSISTANT_LAUNCHER_ATTR, '');
      document.body.appendChild(dockLauncher);

      fixture.componentInstance.open();
      fixture.detectChanges();
      // A real press is pointerdown, then click. Closing on the pointerdown
      // would let the click's toggle reopen the panel straight away.
      dockLauncher.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      expect(assistant.isOpen()).toBe(true);
      launcher.toggle();
      expect(assistant.isOpen()).toBe(false);

      // Anywhere else is still outside.
      fixture.componentInstance.open();
      document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      expect(assistant.isOpen()).toBe(false);
      dockLauncher.remove();
    });

    it('brings the capsule back when the dock leaves (the reader)', () => {
      detach();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeTruthy();
    });
  });

  it('leaves the reader selection surface as the only contextual assistant entry', () => {
    fake.set({ surface: 'reader', route: '/read/b1', bookId: 'b1', selectedText: 'A selected passage' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();
    assistant.requestSurfaceOpen();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeTruthy();
    fake.set({ surface: 'reader', route: '/read/b1', bookId: 'b1', selectedText: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeTruthy();
  });

  it('routes feature-surface open requests through the same focus lifecycle', () => {
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.focus();
    expect(document.activeElement).toBe(outside);

    assistant.requestSurfaceOpen();
    fixture.detectChanges();
    expect(assistant.isOpen()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
    fixture.detectChanges();

    expect(assistant.isOpen()).toBe(false);
    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('does not collapse an already-open expanded assistant on a contextual request', () => {
    fixture.componentInstance.open();
    fixture.detectChanges();
    fixture.componentInstance.toggleExpanded();
    fixture.detectChanges();
    expect(fixture.componentInstance.expanded()).toBe(true);

    assistant.requestSurfaceOpen();
    fixture.detectChanges();

    expect(assistant.isOpen()).toBe(true);
    expect(fixture.componentInstance.expanded()).toBe(true);
  });

  it('closes on Escape and restores the previously focused element', () => {
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    outside.focus();
    expect(document.activeElement).toBe(outside);

    document.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'j', metaKey: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(assistant.isOpen()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
    fixture.detectChanges();

    expect(assistant.isOpen()).toBe(false);
    expect(document.activeElement).toBe(outside);
    outside.remove();
  });

  it('sends the known anchor with the turn and shows the chip label', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookTitle: 'The Magic Mountain',
      bookFormat: 'ebook',
      anchor: { kind: 'pdf_page', value: '183', verified: true },
    });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('A thought about the snow');
    assistant.submit();

    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.message).toBe('A thought about the snow');
    expect(request.request.body.context.anchor).toEqual({
      kind: 'pdf_page',
      value: '183',
      verified: true,
    });
    request.flush(turn());

    expect(assistant.anchorChip()?.label).toBe('The Magic Mountain · p. 183');
  });

  it('asks a physical-book follow-up and still sends when the answer is skipped', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookTitle: 'A Physical Book',
      bookFormat: 'physical',
    });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('A thought without a page');
    assistant.submit();

    http.expectOne('/api/assistant/turn/stream').flush(
      turn({
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-page',
        },
      }),
    );
    expect(assistant.pendingAnchor()?.question).toBe('What page are you on?');
    fixture.detectChanges();

    const skip = fixture.nativeElement.querySelector(
      '[data-testid="assistant-anchor-skip"]',
    ) as HTMLButtonElement;
    expect(skip.classList.contains('nostos-button')).toBe(true);
    skip.click();
    fixture.detectChanges();

    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.message).toBe("I don't know");
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.continuationSkipped).toBe(true);
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(turn());
  });

  it('sends a typed physical page as an unverified anchor', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookTitle: 'A Physical Book',
      bookFormat: 'physical',
    });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('A thought without a page');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(
      turn({
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-page',
        },
      }),
    );

    assistant.updateDraft('42');
    assistant.submit();

    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.message).toBe('42');
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.continuationSkipped).toBe(false);
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(turn({ acknowledgement: 'Saved to A Physical Book.' }));
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('.entry .entry-anchor').textContent,
    ).toContain('A Physical Book · p. 42');
  });

  it('names the book and page in the transcript once a follow-up is answered', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookTitle: 'The Magic Mountain',
      bookFormat: 'physical',
    });
    fixture.detectChanges();

    assistant.open();
    fixture.detectChanges();
    assistant.updateDraft('A thought for The Magic Mountain');
    assistant.submit();
    fixture.detectChanges();

    http.expectOne('/api/assistant/turn/stream').flush(
      turn({
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-page',
        },
      }),
    );
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-transcript"]').textContent,
    ).toContain('What page are you on?');

    assistant.updateDraft('Page 247.');
    assistant.submit();

    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.message).toBe('Page 247.');
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.continuationSkipped).toBe(false);
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(
      turn({ acknowledgement: 'Saved to The Magic Mountain.', capturedNoteId: 'note-1' }),
    );
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('.entry .entry-anchor').textContent,
    ).toContain('The Magic Mountain · p. 247');

    // The captured note shows its topics as pills, with + Topic (issue #1449).
    http.expectOne('/api/topics').flush([]);
    http.expectOne('/api/notes/note-1').flush({ id: 'note-1', content: 'Page 247.\n\n[[Time]]', topicNames: ['Time'] });
    fixture.detectChanges();
    const captured = fixture.nativeElement.querySelector('[data-testid="assistant-capture-topics"]') as HTMLElement;
    expect(captured.querySelector('.topic-chip-name')?.textContent?.trim()).toBe('Time');
    expect(captured.querySelector('[data-testid="topic-row-add"]')?.textContent?.trim()).toBe('Topic');
  });

  it('keeps the pending question and transcript across a close and reopen', () => {
    fake.set({ bookFormat: 'physical', bookTitle: 'The Magic Mountain' });
    fixture.detectChanges();

    assistant.open();
    fixture.detectChanges();
    assistant.updateDraft('A thought for The Magic Mountain');
    assistant.submit();
    fixture.detectChanges();

    http.expectOne('/api/assistant/turn/stream').flush(
      turn({
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-page',
        },
      }),
    );
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-anchor-prompt"]'),
    ).toBeTruthy();

    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeNull();

    fixture.componentInstance.open();
    fixture.detectChanges();
    expect(assistant.pendingAnchor()?.continuationId).toBe('cont-page');
    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-anchor-prompt"]').textContent,
    ).toContain('What page are you on?');
    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-transcript"]').textContent,
    ).toContain('What page are you on?');
    http.expectNone('/api/assistant/turn/stream');
  });

  it('asks for a timestamp when an audiobook is not open in the in-app reader', () => {
    fake.set({ surface: 'book-detail', route: '/library/b1', bookId: 'b1', bookFormat: 'audiobook' });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('A thought');
    assistant.submit();

    http.expectOne('/api/assistant/turn/stream').flush(
      turn({
        anchorPrompt: {
          kind: 'external_audio_timestamp',
          question: "What's the current timestamp?",
          continuationId: 'cont-audio',
        },
      }),
    );

    expect(assistant.pendingAnchor()?.question).toBe("What's the current timestamp?");
  });

  it('sends immediately with no anchor when the format cannot provide one', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookFormat: 'ebook',
    });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('A thought with nowhere to point');
    assistant.submit();

    expect(assistant.pendingAnchor()).toBeNull();
    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(turn());
  });

  it('renders no per-capture processing control in the composer', () => {
    assistant.open();
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-mode-select"]'),
    ).toBeNull();
    expect(fixture.nativeElement.querySelector('.composer-modes')).toBeNull();
    expect(
      fixture.nativeElement.querySelector('.assistant-composer').textContent,
    ).not.toContain('Processing');
  });

  describe('empty conversation', () => {
    it('regroups the empty state into Ask, Capture and Organise doors for the page', () => {
      fake.set({ surface: 'brain', route: '/brain' });
      assistant.open();
      fixture.detectChanges();

      const doors = Array.from<HTMLElement>(
        fixture.nativeElement.querySelectorAll('[data-testid^="assistant-door-"]'),
      );
      expect(doors.map((door) => door.dataset['door'])).toEqual(['ask', 'capture', 'organise']);
      expect(doors[0].textContent).toContain('What connects my topics?');
      expect(doors[1].textContent).toContain('Save this thought:');
      expect(doors[2].textContent).toContain('Move my Le Guin books into a collection');
      expect(fixture.nativeElement.querySelector('[data-testid="assistant-empty"]')?.textContent).toContain(
        "It can't delete books or notes, or change settings.",
      );
    });

    it('prefills the composer from a door and never sends', () => {
      fake.set({ surface: 'library', route: '/library' });
      assistant.open();
      fixture.detectChanges();

      (fixture.nativeElement.querySelector(
        '[data-testid="assistant-example-organise-to-read"]',
      ) as HTMLButtonElement).click();
      fixture.detectChanges();

      const composer = fixture.nativeElement.querySelector(
        '[data-testid="assistant-composer"]',
      ) as HTMLTextAreaElement;
      expect(composer.value).toBe('Add Piranesi to To read');
      expect(assistant.draft()).toBe('Add Piranesi to To read');
      expect(assistant.entries()).toEqual([]);
      expect(fixture.nativeElement.querySelector('[data-testid="assistant-empty"]')).not.toBeNull();
      http.expectNone('/api/assistant/turn/stream');
    });

    it('prefills a capture as a shape to finish, not as a sent note', () => {
      fake.set({ surface: 'reader', route: '/read/b1', bookId: 'b1', bookTitle: 'Dune' });
      assistant.open();
      fixture.detectChanges();

      (fixture.nativeElement.querySelector(
        '[data-testid="assistant-example-capture-thought"]',
      ) as HTMLButtonElement).click();
      fixture.detectChanges();

      const composer = fixture.nativeElement.querySelector(
        '[data-testid="assistant-composer"]',
      ) as HTMLTextAreaElement;
      expect(composer.value).toBe('Save this thought: ');
      expect(assistant.entries()).toEqual([]);
      http.expectNone('/api/assistant/turn/stream');
    });
  });

  it('names the book in the composer, unless a source chip already does', () => {
    fake.set({ surface: 'book-detail', route: '/library/b1', bookId: 'b1', bookTitle: 'Dune' });
    assistant.open();
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector('[data-testid="assistant-reading-chip"]');
    expect(chip?.textContent?.trim()).toBe('Dune');
    // The header no longer carries a second copy of the book name.
    expect(fixture.nativeElement.querySelector('.assistant-header')?.textContent).not.toContain('Dune');

    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      bookTitle: 'Dune',
      bookFormat: 'ebook',
      anchor: { kind: 'pdf_page', value: '12', verified: true },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-reading-chip"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-anchor-chip"]')).toBeTruthy();
  });

  it('keeps the speaker names for screen readers while the layout tells them apart', () => {
    assistant.open();
    assistant.updateDraft('Who are you?');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'The Nostos assistant.' }));
    fixture.detectChanges();

    for (const id of ['assistant-entry-user-label', 'assistant-entry-assistant-label']) {
      expect(fixture.nativeElement.querySelector(`[data-testid="${id}"]`).classList).toContain('visually-hidden');
    }
  });

  it('renders a speaker label for both sides of the transcript', () => {
    assistant.open();
    assistant.updateDraft('Who are you?');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'The Nostos assistant.' }));
    fixture.detectChanges();

    const userLabel = fixture.nativeElement.querySelector(
      '[data-testid="assistant-entry-user-label"]',
    );
    const assistantLabel = fixture.nativeElement.querySelector(
      '[data-testid="assistant-entry-assistant-label"]',
    );

    expect(userLabel).toBeTruthy();
    expect(assistantLabel).toBeTruthy();
    expect(userLabel.textContent).toContain('You');
    expect(assistantLabel.textContent).toContain('Nostos');
  });

  it('renders the complete assistant Markdown surface safely and keeps user text literal', () => {
    assistant.open();
    assistant.updateDraft('**Keep this user text literal**');
    assistant.submit();

    const reply = [
      '# Reading plan',
      '',
      'Paragraph with **bold**, *italics*, ~~removed~~, `inline code`, and [a link](https://example.com).',
      '',
      '1. First',
      '   - Nested item',
      '2. Second',
      '',
      '- [x] Finished',
      '- [ ] Still reading',
      '',
      '> Outer quote',
      '>> Nested quote',
      '',
      '| Shelf | Books |',
      '| --- | ---: |',
      '| Classics | 18 |',
      '',
      '```ts',
      'const answer = 42;',
      '```',
      '',
      '---',
      '',
      '![cover](https://tracker.invalid/cover.png)',
      '[unsafe](javascript:alert(1))',
      '<script>globalThis.__nostosXss = true</script>',
      '<img src="https://tracker.invalid/pixel" onerror="globalThis.__nostosXss = true">',
      '',
      '**unfinished',
    ].join('\n');

    http.expectOne('/api/assistant/turn/stream').flush(turn({ reply }));
    fixture.detectChanges();

    const userEntry = fixture.nativeElement.querySelector('.entry-user .entry-text') as HTMLElement;
    expect(userEntry.textContent).toContain('**Keep this user text literal**');
    expect(userEntry.querySelector('strong')).toBeNull();

    const rendered = fixture.nativeElement.querySelector(
      '[data-testid="assistant-entry-markdown"]',
    ) as HTMLElement;
    expect(rendered).toBeTruthy();
    expect(rendered.querySelector('h1')?.textContent).toContain('Reading plan');
    expect(rendered.querySelector('strong')?.textContent).toBe('bold');
    expect(rendered.querySelector('em')?.textContent).toBe('italics');
    expect(rendered.querySelector('del')?.textContent).toBe('removed');
    expect(rendered.querySelector('ol')).toBeTruthy();
    expect(rendered.querySelector('ol ul')).toBeTruthy();
    expect(rendered.querySelector('blockquote blockquote')).toBeTruthy();
    expect(rendered.querySelector('table')).toBeTruthy();
    expect(rendered.querySelector('pre code')?.textContent).toContain('const answer = 42;');
    expect(rendered.querySelector('hr')).toBeTruthy();

    expect(rendered.querySelector('input')).toBeNull();
    expect(rendered.textContent).toContain('☑');
    expect(rendered.textContent).toContain('☐');

    expect(rendered.querySelector('img')).toBeNull();
    expect(rendered.textContent).toContain('[Image omitted: cover]');

    expect(rendered.querySelector('script')).toBeNull();
    expect(rendered.textContent).toContain('<script>globalThis.__nostosXss = true</script>');
    expect(rendered.textContent).toContain('<img src="https://tracker.invalid/pixel"');
    const unsafeLink = Array.from(rendered.querySelectorAll('a')).find(
      (link) => link.textContent === 'unsafe',
    ) as HTMLAnchorElement | undefined;
    expect(unsafeLink).toBeTruthy();
    expect(unsafeLink?.getAttribute('href')?.startsWith('javascript:')).toBe(false);

    expect(rendered.textContent).toContain('**unfinished');
  });

  describe('manual Stop (issue #648)', () => {
    function transcriptText(): string {
      return fixture.nativeElement
        .querySelector('[data-testid="assistant-transcript"]')
        ?.textContent ?? '';
    }

    function stoppedCount(): number {
      return transcriptText().match(/Stopped/g)?.length ?? 0;
    }

    /** Starts a streamed turn, presses Stop, and delivers one cancelled terminal event. */
    function stopTurn(message: string, executedCapabilities: string[] = []): string {
      assistant.open();
      fixture.detectChanges();
      assistant.updateDraft('Mark it as a favourite');
      assistant.submit();
      fixture.detectChanges();
      const request = http.expectOne('/api/assistant/turn/stream');
      const turnId = request.request.body.turnId as string;

      const startedLine = JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n';
      request.event({
        type: HttpEventType.DownloadProgress,
        loaded: startedLine.length,
        partialText: startedLine,
      });
      fixture.detectChanges();

      assistant.stopActiveTurn();
      http.expectOne('/api/assistant/turn/cancel')
        .flush({ accepted: true, state: 'cancel_requested' });

      const error = { code: 'assistant_turn_cancelled', message };
      request.flush(startedLine + JSON.stringify({
        turnId,
        sequence: 2,
        kind: 'cancelled',
        failure: { ...error, retryable: false },
        response: turn({ reply: '', error, executedCapabilities }),
      }) + '\n');
      fixture.detectChanges();
      return turnId;
    }

    it('renders one cancellation indication when nothing was committed', () => {
      const turnId = stopTurn('Stopped.');

      expect(stoppedCount()).toBe(1);
      const artifacts = assistant.entries()
        .filter((entry) => entry.turnId === turnId)
        .flatMap((entry) => entry.artifacts ?? []);
      expect(artifacts).toContainEqual(expect.objectContaining({
        kind: 'failure',
        code: 'assistant_turn_cancelled',
        state: 'cancelled',
      }));
    });

    it('keeps the single indication truthful after a committed change', () => {
      stopTurn('Stopped. Changes that already completed remain applied.', ['library_update_book']);

      expect(stoppedCount()).toBe(1);
      expect(transcriptText()).toContain('Changes that already completed remain applied.');
      expect(transcriptText()).toContain('Changed · book updated');
    });

    it('does not multiply the marker when the session is restored', async () => {
      stopTurn('Stopped.');
      expect(stoppedCount()).toBe(1);

      http.verify();
      TestBed.resetTestingModule();
      await configureComponent();
      assistant.open();
      fixture.detectChanges();

      expect(stoppedCount()).toBe(1);
    });
  });

  describe('execution ceiling after a committed change (issue #647)', () => {
    const PARTIAL = 'assistant_turn_partially_completed';

    function transcript(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-testid="assistant-transcript"]');
    }

    /** Sends one streamed turn that ends with the given terminal event. */
    function finishTurn(kind: 'completed' | 'failed', response: AssistantTurnResponse): void {
      assistant.open();
      fixture.detectChanges();
      assistant.updateDraft('the epub version i guess?');
      assistant.submit();
      fixture.detectChanges();
      const request = http.expectOne('/api/assistant/turn/stream');
      const turnId = request.request.body.turnId as string;
      const failure = response.error
        ? { code: response.error.code, message: response.error.message, retryable: false }
        : undefined;
      request.flush(
        JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
        + JSON.stringify({ turnId, sequence: 2, kind, failure, response }) + '\n',
      );
      fixture.detectChanges();
    }

    it('shows one success outcome when the change completed before the ceiling', () => {
      finishTurn('completed', turn({
        reply: 'Done.',
        executedCapabilities: ['library_update_book'],
        error: null,
      }));

      const text = transcript().textContent ?? '';
      expect(text).toContain('Done.');
      expect(text).toContain('Changed · book updated');
      expect(text).not.toContain('Failed');
      expect(text).not.toContain('execution limit');
      expect(transcript().querySelector('.entry-error')).toBeNull();
    });

    it('shows committed changes as applied and the rest as not finished, never as failed', () => {
      const message =
        "I made the changes listed below, but reached this turn's execution limit before I could finish the rest. Send another message to continue.";
      finishTurn('failed', turn({
        reply: message,
        executedCapabilities: ['library_update_book'],
        error: { code: PARTIAL, message },
      }));

      const text = transcript().textContent ?? '';
      expect(text).toContain('Changed · book updated');
      expect(text).toContain('Not finished');
      expect(text).not.toContain('Failed');
      expect(text).not.toContain(PARTIAL);
      expect(text.match(/execution limit/g)?.length).toBe(1);
      expect(transcript().querySelector('.entry-error')).toBeNull();
      expect(assistant.lastError()).toBeNull();
    });

    it('still reports a failure when nothing was committed before the ceiling', () => {
      const message = 'This turn reached its execution limit before it could finish.';
      finishTurn('failed', turn({
        reply: "I reached this turn's execution limit before I could finish. Send another message to continue.",
        error: { code: 'assistant_execution_budget_exhausted', message },
      }));

      const failures = transcript().querySelectorAll('[data-testid="assistant-failure"]');
      expect(failures.length).toBe(1);
      expect(failures[0].textContent).toContain("I reached this turn's execution limit");
      expect(failures[0].querySelector('code')?.textContent)
        .toBe('assistant_execution_budget_exhausted');
      expect(transcript().textContent).not.toContain('Failed ·');
    });
  });

  describe('one failure, one next step, one retry (issues #1556, #1560)', () => {
    type ActionKind = 'retry' | 'connections' | 'settings' | 'allowance' | 'book';

    interface FailureCase {
      code: string;
      message: string;
      retryable: boolean;
      action: ActionKind;
    }

    const ACTION_LABELS: Record<ActionKind, string> = {
      retry: 'Try again',
      connections: 'Open AI connections',
      settings: 'Open Settings',
      allowance: 'Manage allowance',
      book: 'Open the book',
    };

    // Every code the backend can put in a failed event, plus the client's
    // transport codes. The message column is the backend's own sentence.
    const CASES: FailureCase[] = [
      { code: 'assistant_disabled', message: 'Ask Nostos is disabled by this Nostos host.', retryable: false, action: 'settings' },
      { code: 'assistant_not_configured', message: 'Ask Nostos needs an AI provider configured in Settings.', retryable: false, action: 'connections' },
      { code: 'assistant_access_denied', message: 'Ask Nostos is not available for this account.', retryable: false, action: 'allowance' },
      { code: 'assistant_permission_denied', message: 'The AI service did not accept its key. Check the AI provider in Settings.', retryable: false, action: 'connections' },
      { code: 'assistant_rate_limited', message: 'Ask Nostos is temporarily busy. Try again shortly.', retryable: true, action: 'retry' },
      { code: 'assistant_provider_timeout', message: 'Ask Nostos took too long to answer. Try again.', retryable: true, action: 'retry' },
      { code: 'assistant_provider_error', message: 'The AI service could not complete this turn. Try again.', retryable: true, action: 'retry' },
      { code: 'assistant_response_invalid', message: 'The AI service sent a response this app could not read.', retryable: true, action: 'retry' },
      { code: 'ai_access_denied', message: 'Ask Nostos is not available for this account.', retryable: false, action: 'allowance' },
      { code: 'ai_rate_limited', message: 'Ask Nostos is temporarily busy. Try again shortly.', retryable: true, action: 'retry' },
      { code: 'ai_usage_limit_reached', message: 'Your AI allowance is used up for now.', retryable: false, action: 'allowance' },
      { code: 'ai_disabled', message: 'Managed AI is temporarily disabled.', retryable: true, action: 'retry' },
      { code: 'ai_temporarily_unavailable', message: 'Ask Nostos is temporarily unavailable.', retryable: true, action: 'retry' },
      { code: 'assistant_unknown_capability', message: 'That action is not available in this version.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_unavailable', message: 'These changes are no longer available.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_not_found', message: 'These changes could not be found.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_token_mismatch', message: 'These changes can no longer be applied from here.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_not_reviewable', message: 'These changes can no longer be reviewed.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_not_undoable', message: 'These changes can no longer be undone here.', retryable: false, action: 'book' },
      { code: 'assistant_changeset_busy', message: 'These changes are already being applied.', retryable: false, action: 'book' },
      { code: 'assistant_not_permitted', message: 'That action is not permitted.', retryable: false, action: 'book' },
      { code: 'assistant_invalid_arguments', message: 'The requested change could not be completed.', retryable: false, action: 'book' },
      { code: 'assistant_not_found', message: 'The requested item could not be found.', retryable: false, action: 'book' },
      { code: 'assistant_continuation_not_found', message: 'That follow-up has expired. Nothing was changed.', retryable: false, action: 'book' },
      { code: 'assistant_continuation_mismatch', message: 'That follow-up belongs to a different conversation. Nothing was changed.', retryable: false, action: 'book' },
      { code: 'assistant_continuation_answer_required', message: 'A book is required before this capture can be saved.', retryable: false, action: 'book' },
      { code: 'invalid_processing_mode', message: 'That processing mode is not available.', retryable: false, action: 'book' },
      { code: 'assistant_turn_already_running', message: 'This turn is already running.', retryable: false, action: 'book' },
      { code: 'assistant_turn_cancelled', message: 'Stopped.', retryable: false, action: 'book' },
      { code: 'assistant_execution_budget_exhausted', message: 'This turn reached its execution limit before it could finish.', retryable: false, action: 'book' },
      // `assistant_turn_partially_completed` is deliberately absent: it is an
      // incomplete turn with its own "Not finished" label, never a failure
      // block. `assistant-failure.spec.ts` still pins its mapping.
      { code: 'assistant_unverified_completion_claim', message: 'That change was not confirmed by the server.', retryable: false, action: 'book' },
      { code: 'assistant_no_evidence', message: 'I could not find usable evidence for that in your Nostos material.', retryable: false, action: 'book' },
      { code: 'assistant_source_indexing_pending', message: 'This source is still being indexed. Try again when it is ready.', retryable: false, action: 'book' },
      { code: 'assistant_source_indexing_failed', message: 'Text indexing failed for this source, so Ask Nostos cannot search it yet.', retryable: false, action: 'book' },
      { code: 'assistant_source_indexing_unsupported', message: 'This source cannot be searched as text in its current format.', retryable: false, action: 'book' },
      { code: 'assistant_network_error', message: 'The connection to Ask Nostos was interrupted. Your message is ready to retry.', retryable: true, action: 'retry' },
      { code: 'assistant_offline', message: 'You are offline. Reconnect and retry this message.', retryable: true, action: 'retry' },
      { code: 'assistant_stream_stalled', message: 'Ask Nostos did not answer in time. Your message is ready to retry.', retryable: true, action: 'retry' },
      { code: 'assistant_turn_failed', message: 'Ask Nostos could not complete this turn.', retryable: true, action: 'retry' },
    ];

    function transcript(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-testid="assistant-transcript"]');
    }

    function failures(): HTMLElement[] {
      return Array.from(
        fixture.nativeElement.querySelectorAll('[data-testid="assistant-failure"]'),
      ) as HTMLElement[];
    }

    /** One streamed turn that ends in the given failure. Returns its TurnId. */
    function sendFailure(
      code: string,
      message: string,
      retryable: boolean,
      response: Partial<AssistantTurnResponse> | null = null,
    ): string {
      assistant.open();
      fixture.detectChanges();
      assistant.updateDraft(`Question for ${code}`);
      assistant.submit();
      fixture.detectChanges();

      const request = http.expectOne('/api/assistant/turn/stream');
      const turnId = request.request.body.turnId as string;
      const body = JSON.stringify({
        turnId,
        sequence: 2,
        kind: 'failed',
        failure: { code, message, retryable },
        ...(response ? { response } : {}),
      });
      request.flush(
        JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n' + body + '\n',
      );
      fixture.detectChanges();
      return turnId;
    }

    it('renders a failure once: one sentence, one action, and the code behind Details', () => {
      const message = 'Ask Nostos took too long to answer. Try again.';
      sendFailure('assistant_provider_timeout', message, true);

      expect(failures().length).toBe(1);
      const block = failures()[0];
      expect(block.textContent?.match(/Ask Nostos took too long to answer/g)?.length).toBe(1);
      expect(transcript().textContent).not.toContain('Failed ·');

      const text = block.querySelector('.entry-failure-text') as HTMLElement;
      expect(text.textContent?.trim()).toBe(message);
      expect(text.getAttribute('role')).toBe('status');
      expect(text.getAttribute('aria-live')).toBe('polite');

      const details = block.querySelector('details') as HTMLDetailsElement;
      expect(details).toBeTruthy();
      expect(details.open).toBe(false);
      expect(details.querySelector('summary')?.textContent?.trim()).toBe('Details');
      expect(details.querySelector('code')?.textContent).toBe('assistant_provider_timeout');
      expect(block.querySelectorAll('code').length).toBe(1);

      // Nothing prints the raw code outside its Details disclosure.
      const statuses = Array.from(transcript().querySelectorAll('.entry-status'));
      for (const status of statuses) {
        expect(status.textContent).not.toContain('assistant_provider_timeout');
      }

      expect(block.querySelector('[data-testid="assistant-retry"]')?.textContent?.trim())
        .toBe('Try again');
    });

    it('keeps a capture that committed before the failure above the failure block', () => {
      const message = 'The AI service could not complete this turn. Try again.';
      const turnId = sendFailure('assistant_provider_error', message, true, turn({
        reply: '',
        acknowledgement: 'Saved to The Magic Mountain.',
        capturedNoteId: 'note-1558',
        executedCapabilities: [],
        suggestions: [],
        sources: [],
        evidence: [],
      }));

      http.expectOne('/api/topics').flush([]);
      http.expectOne('/api/notes/note-1558').flush({
        id: 'note-1558', content: 'A thought for The Magic Mountain', topicNames: [],
      });
      fixture.detectChanges();

      // The committed outcome reads first: saved, then the failure with its one
      // retry. Nothing is reported twice (#1558 on top of #1556).
      const text = transcript().textContent ?? '';
      expect(text).toContain('Saved to The Magic Mountain.');
      expect(text).toContain('Saved note');
      expect(text.indexOf('Saved to The Magic Mountain.')).toBeLessThan(text.indexOf(message));
      expect(text.indexOf('Saved note')).toBeLessThan(text.indexOf(message));
      expect(failures().length).toBe(1);
      expect(text.match(/could not complete this turn/g)?.length).toBe(1);
      expect(text).not.toContain('Failed ·');

      const retry = failures()[0].querySelector('[data-testid="assistant-retry"]') as HTMLButtonElement;
      expect(retry?.textContent?.trim()).toBe('Try again');

      // Try again is a replay of the same turn, so the server's receipts answer
      // instead of writing the note a second time.
      retry.click();
      const request = http.expectOne('/api/assistant/turn/stream');
      expect(request.request.body.turnId).toBe(turnId);
      request.flush(turn({ reply: 'Here is the answer, on the second delivery.' }));
      fixture.detectChanges();

      expect(failures().length).toBe(0);
      expect(transcript().textContent).toContain('Here is the answer, on the second delivery.');
    });

    it('does not render a second failure label when the response carries the sentence', () => {
      const message = 'I could not find usable evidence for that in your Nostos material.';
      sendFailure('assistant_no_evidence', message, false, turn({
        reply: message,
        error: { code: 'assistant_no_evidence', message },
      }));

      expect(failures().length).toBe(1);
      expect(transcript().textContent?.match(/could not find usable evidence/g)?.length).toBe(1);
      expect(transcript().textContent).not.toContain('Failed ·');
      expect(failures()[0].querySelector('code')?.textContent).toBe('assistant_no_evidence');
    });

    it('gives every backend failure code its sentence and its one action', () => {
      allowance.setAccountUrl('https://nostos.page/account');

      for (const item of CASES) {
        assistant.newConversation();
        fixture.detectChanges();

        sendFailure(item.code, item.message, item.retryable);

        const block = failures()[0];
        expect(block, item.code).toBeTruthy();
        expect(block.querySelector('.entry-failure-text')?.textContent?.trim(), item.code)
          .toBe(item.message);

        const action = block.querySelector(
          '[data-testid="assistant-retry"], [data-testid="assistant-failure-allowance"], [data-testid="assistant-failure-action"]',
        ) as HTMLElement | null;
        expect(action, item.code).toBeTruthy();
        expect(action?.textContent?.trim(), item.code).toBe(ACTION_LABELS[item.action]);
      }
    });

    it('keeps the account link and the renewal date for an exhausted allowance', () => {
      allowance.setAccountUrl('https://nostos.page/account');
      allowance.setUsage({
        state: 'exhausted',
        renewsAtUtc: '2026-12-01T00:00:00Z',
        refill: { available: true, state: 'active' },
      });

      sendFailure('ai_usage_limit_reached', 'Your AI allowance is used up for now.', false);

      const block = failures()[0];
      expect(block.textContent).toContain('Allowance renews 1 Dec 2026.');
      const link = block.querySelector('[data-testid="assistant-failure-allowance"]') as HTMLAnchorElement;
      expect(link).toBeTruthy();
      expect(link.textContent?.trim()).toBe('Manage allowance');
      expect(link.getAttribute('href')).toBe('https://nostos.page/account');
      expect(link.getAttribute('target')).toBe('_blank');
      expect(allowance.ensureUsage).toHaveBeenCalled();
    });

    it('opens the AI connections section for a provider configuration failure', () => {
      sendFailure(
        'assistant_permission_denied',
        'The AI service did not accept its key. Check the AI provider in Settings.',
        false,
      );

      const action = failures()[0].querySelector('[data-testid="assistant-failure-action"]') as HTMLButtonElement;
      action.click();

      expect(router.navigate).toHaveBeenCalledWith(
        ['/settings'],
        { queryParams: { section: 'connections' } },
      );
    });

    it('opens the turn\'s own book for an indexing failure', () => {
      sendFailure(
        'assistant_source_indexing_pending',
        'This source is still being indexed. Try again when it is ready.',
        false,
      );

      const action = failures()[0].querySelector('[data-testid="assistant-failure-action"]') as HTMLButtonElement;
      action.click();

      expect(router.navigate).toHaveBeenCalledWith(['/read', 'b1']);
    });

    it('replays the stored turn with the same TurnId and does not add a user entry', () => {
      vi.useFakeTimers();
      try {
        const turnId = sendFailure(
          'assistant_provider_timeout',
          'Ask Nostos took too long to answer. Try again.',
          true,
        );
        const userEntries = () => assistant.entries().filter((entry) => entry.kind === 'user');

        const retry = failures()[0].querySelector('[data-testid="assistant-retry"]') as HTMLButtonElement;
        retry.click();

        const request = http.expectOne('/api/assistant/turn/stream');
        expect(request.request.body.turnId).toBe(turnId);
        expect(request.request.body.idempotencyKey).toBe(turnId);

        vi.advanceTimersByTime(0);
        fixture.detectChanges();
        expect(document.activeElement).toBe(transcript());
        expect(userEntries().length).toBe(1);
        expect(failures().length).toBe(0);

        request.flush(
          JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
          + JSON.stringify({
            turnId,
            sequence: 2,
            kind: 'completed',
            response: turn({ reply: 'Here it is.' }),
          }) + '\n',
        );
        fixture.detectChanges();

        expect(failures().length).toBe(0);
        expect(transcript().textContent).toContain('Here it is.');
      } finally {
        vi.useRealTimers();
      }
    });

    it('turns a silent stream into the retry block at the watchdog limit, not before', () => {
      vi.useFakeTimers();
      try {
        assistant.open();
        fixture.detectChanges();
        assistant.updateDraft('Are you there?');
        assistant.submit();
        fixture.detectChanges();

        const request = http.expectOne('/api/assistant/turn/stream');
        const turnId = request.request.body.turnId as string;
        request.event({
          type: HttpEventType.DownloadProgress,
          partialText: JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n',
        } as never);
        fixture.detectChanges();

        vi.advanceTimersByTime(ASSISTANT_PENDING_DELAY_MS);
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('[data-testid="assistant-pending"]')).toBeTruthy();

        // One millisecond before every server clock has passed: still waiting.
        vi.advanceTimersByTime(ASSISTANT_TURN_WATCHDOG_MS - ASSISTANT_PENDING_DELAY_MS - 1);
        fixture.detectChanges();
        expect(failures().length).toBe(0);
        expect(fixture.nativeElement.querySelector('[data-testid="assistant-pending"]')).toBeTruthy();

        vi.advanceTimersByTime(1);
        fixture.detectChanges();

        expect(fixture.nativeElement.querySelector('[data-testid="assistant-pending"]')).toBeNull();
        expect(failures().length).toBe(1);
        expect(failures()[0].textContent).toContain('Ask Nostos did not answer in time.');
        expect(failures()[0].querySelector('[data-testid="assistant-retry"]')).toBeTruthy();
        expect(assistant.sending()).toBe(false);
        // The request is cancelled, so a late server response is never applied.
        expect(request.cancelled).toBe(true);
        vi.advanceTimersByTime(ASSISTANT_TURN_WATCHDOG_MS);
        fixture.detectChanges();
        expect(failures().length).toBe(1);
      } finally {
        vi.useRealTimers();
      }
    });

    it('does not leave a dead Try again on a failure a later turn superseded', () => {
      sendFailure(
        'assistant_provider_timeout',
        'Ask Nostos took too long to answer. Try again.',
        true,
      );

      // Sending a new message clears the stored turn; the old failure keeps its
      // sentence and Details but must not offer a button that cannot replay it.
      assistant.updateDraft('A different question');
      assistant.submit();
      fixture.detectChanges();
      const next = http.expectOne('/api/assistant/turn/stream');
      next.flush(turn({ reply: 'A different answer.' }));
      fixture.detectChanges();

      const oldFailure = failures().find((block) =>
        block.textContent?.includes('Ask Nostos took too long')) as HTMLElement;
      expect(oldFailure).toBeTruthy();
      expect(oldFailure.querySelector('[data-testid="assistant-retry"]')).toBeNull();
      expect(oldFailure.querySelector('details')).toBeTruthy();
    });
  });

  describe('turn activity (issue #564)', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());
    function transcript(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-testid="assistant-transcript"]');
    }

    function pending(): HTMLElement | null {
      return fixture.nativeElement.querySelector('[data-testid="assistant-pending"]');
    }

    /** Dispatch a turn and leave it in flight, returning its request handle. */
    function sendInFlight() {
      assistant.open();
      fixture.detectChanges();
      assistant.updateDraft('Are you there?');
      assistant.submit();
      fixture.detectChanges();
      return http.expectOne('/api/assistant/turn/stream');
    }

    function revealPending(): void {
      vi.advanceTimersByTime(ASSISTANT_PENDING_DELAY_MS);
      fixture.detectChanges();
    }

    it('shows the pending entry after the user entry while a turn is in flight', () => {
      const request = sendInFlight();
      revealPending();

      const indicator = pending();
      expect(indicator).toBeTruthy();

      const entries = Array.from(transcript().querySelectorAll('.entry')) as HTMLElement[];
      const userEntry = transcript().querySelector('[data-testid="assistant-entry-user-label"]')
        ?.closest('.entry') as HTMLElement;
      expect(userEntry).toBeTruthy();
      expect(entries.indexOf(indicator!)).toBeGreaterThan(entries.indexOf(userEntry));

      request.flush(turn());
    });

    it('removes the pending entry when the response arrives', () => {
      const request = sendInFlight();
      revealPending();
      expect(pending()).toBeTruthy();

      request.flush(turn({ reply: 'Here.' }));
      fixture.detectChanges();

      expect(pending()).toBeNull();
    });

    it('removes the pending entry when the request fails', () => {
      const request = sendInFlight();
      revealPending();
      expect(pending()).toBeTruthy();

      request.flush('', { status: 503, statusText: 'Service Unavailable' });
      fixture.detectChanges();

      expect(pending()).toBeNull();
      expect(assistant.sending()).toBe(false);
    });

    it('exposes the state as accessible text and hides the decorative dots', () => {
      const request = sendInFlight();
      revealPending();

      const indicator = pending()!;
      // The speaker name is visually hidden too; the state text is in the activity row.
      const hidden = indicator.querySelector('.pending-row .visually-hidden');
      expect(hidden).toBeTruthy();
      expect(hidden!.textContent?.trim()).toContain('Working');
      expect(indicator.getAttribute('role')).toBe('status');
      expect(indicator.getAttribute('aria-live')).toBe('polite');

      const dots = indicator.querySelector('.thinking-dots');
      expect(dots).toBeTruthy();
      expect(dots!.getAttribute('aria-hidden')).toBe('true');
      expect(dots!.querySelectorAll('.thinking-dot').length).toBe(3);

      expect(indicator.querySelector('[data-testid="assistant-stop-turn"]')).toBeNull();

      const turnId = request.request.body.turnId as string;
      const startedLine = JSON.stringify({
        turnId,
        sequence: 1,
        kind: 'started',
      }) + '\n';
      request.event({
        type: HttpEventType.DownloadProgress,
        loaded: startedLine.length,
        partialText: startedLine,
      });
      fixture.detectChanges();

      expect(
        pending()!.querySelector('[data-testid="assistant-stop-turn"]'),
      ).toBeTruthy();

      request.flush(startedLine + JSON.stringify({
        turnId,
        sequence: 2,
        kind: 'completed',
        response: turn(),
      }) + '\n');
    });
  });
  it('renders suggestion chips and links the chosen existing topic without a second approval', () => {
    fake.set({
      surface: 'reader',
      route: '/read/b1',
      bookId: 'b1',
      brainReviewNoteId: 'note-1',
    });
    fixture.detectChanges();

    assistant.open();
    assistant.updateDraft('Where does this note belong?');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({
      suggestions: [
        { kind: 'topic', label: 'Mountains', reason: 'Evidence relationship.', value: 'c-alpha', noteId: 'note-1' },
      ],
    }));
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector(
      '[data-testid="assistant-suggestion"]',
    ) as HTMLButtonElement;
    expect(chip).toBeTruthy();
    expect(chip.textContent).toContain('Mountains');

    chip.click();
    fixture.detectChanges();

    const request = http.expectOne('/api/assistant/turn/stream');
    expect(request.request.body.message).toContain('Mountains');
    request.flush(turn({ reply: 'Linked the note to Mountains.', changeset: null }));
    fixture.detectChanges();

    expect(assistant.pendingReview()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-changeset"]')).toBeNull();
  });

  it('shows a review card in the turn that proposed it and applies it from the card', () => {
    assistant.open();
    assistant.updateDraft('Delete everything and regroup by theme.');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({
      reply: "Here's what I'd change. Nothing changes until you apply it.",
      changeset: reviewChangeset(),
    }));
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector('[data-testid="assistant-changeset"]') as HTMLElement;
    expect(card).toBeTruthy();
    expect(card.getAttribute('data-status')).toBe('review');
    expect(card.textContent).toContain('Review before applying');
    expect(card.textContent).toContain('This will delete 3 collections, create 2 collections and refile 1 book.');
    expect(card.textContent).toContain('Deletes 3 collections that hold 5 books between them.');
    expect(card.textContent).toContain('Or reply “yes” or “no”.');
    expect(card.querySelectorAll('[data-testid="assistant-changeset-step-toggle"]').length).toBe(3);

    const apply = card.querySelector('[data-testid="assistant-changeset-apply"]') as HTMLButtonElement;
    expect(apply.classList.contains('nostos-button--primary')).toBe(true);
    apply.click();
    fixture.detectChanges();

    const request = http.expectOne('/api/assistant/changesets/cs-9/apply');
    expect(request.request.body).toEqual({ actionToken: 'tok-9', excluded: [], diffHash: 'hash-9' });
    request.flush({
      success: true, errorCode: null, errorMessage: null, conflicts: [],
      changeset: reviewChangeset({ status: 'applied', canUndo: true }),
    });
    fixture.detectChanges();

    const applied = fixture.nativeElement.querySelector('[data-testid="assistant-changeset"]') as HTMLElement;
    expect(applied.getAttribute('data-status')).toBe('applied');
    expect(applied.textContent).toContain('I deleted 3 collections, created 2 collections and refiled 1 book.');
    expect(applied.querySelector('[data-testid="assistant-changeset-undo"]')).toBeTruthy();
    expect(applied.querySelector('[data-testid="assistant-changeset-apply"]')).toBeNull();
  });

  it('unticking a step asks the server what the rest would do and greys out its dependants', () => {
    assistant.open();
    assistant.updateDraft('Regroup.');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({ changeset: reviewChangeset() }));
    fixture.detectChanges();

    const toggles = fixture.nativeElement.querySelectorAll(
      '[data-testid="assistant-changeset-step-toggle"]',
    ) as NodeListOf<HTMLInputElement>;
    toggles[1].click();
    fixture.detectChanges();

    const preview = http.expectOne('/api/assistant/changesets/cs-9/preview');
    expect(preview.request.body).toEqual({ actionToken: 'tok-9', excluded: [1] });
    const steps = reviewChangeset().steps.map((step) => ({ ...step, included: step.index === 0 }));
    preview.flush({
      success: true, errorCode: null, errorMessage: null, conflicts: [],
      changeset: reviewChangeset({ steps }),
    });
    fixture.detectChanges();

    const after = fixture.nativeElement.querySelectorAll(
      '[data-testid="assistant-changeset-step-toggle"]',
    ) as NodeListOf<HTMLInputElement>;
    expect([...after].map((toggle) => toggle.checked)).toEqual([true, false, false]);
    expect(after[2].disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('1 of 3 steps');
  });

  it('opens every change in the details list', () => {
    assistant.open();
    assistant.updateDraft('Regroup.');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({ changeset: reviewChangeset() }));
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="assistant-changeset-details-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const details = fixture.nativeElement.querySelector('[data-testid="assistant-changeset-details"]') as HTMLElement;
    expect(details.textContent).toContain('Delete “Fiction”');
    expect(details.textContent).toContain('held 2 books');
    expect(details.textContent).toContain('New “Science fiction”');
    expect(details.textContent).toContain('Dune');
    expect(details.textContent).toContain('+ Science fiction');
    expect(details.textContent).toContain('− Fiction');
  });

  it('leaves the note unlinked when the user dismisses the suggestions', () => {
    assistant.open();
    assistant.updateDraft('Suggest a topic');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({
      suggestions: [
        { kind: 'topic', label: 'Mountains', reason: 'Evidence relationship.', value: 'c-alpha', noteId: 'note-1' },
      ],
    }));
    fixture.detectChanges();

    const none = fixture.nativeElement.querySelector(
      '[data-testid="assistant-suggestion-none"]',
    ) as HTMLButtonElement;
    expect(none).toBeTruthy();
    expect(none.classList.contains('nostos-button')).toBe(true);

    none.click();
    fixture.detectChanges();

    expect(assistant.suggestions()).toEqual([]);
    expect(assistant.entries().at(-1)?.suggestions).toEqual([]);
    http.expectNone('/api/assistant/turn/stream');
  });

  describe('following the newest turn (issue #300)', () => {
    const PANE_HEIGHT = 400;
    const CONTENT_HEIGHT = 1200;
    const END = CONTENT_HEIGHT - PANE_HEIGHT;
    let contentHeight = CONTENT_HEIGHT;
    let originalResizeObserver: typeof ResizeObserver | undefined;
    let resizeObserverCallback: (() => void) | null = null;
    const observedResizeElements = new Set<Element>();

    const positions = new WeakMap<Element, number>();

    function isPane(element: Element): boolean {
      return element.getAttribute('data-testid') === 'assistant-body';
    }

    beforeEach(() => {
      contentHeight = CONTENT_HEIGHT;
      resizeObserverCallback = null;
      observedResizeElements.clear();
      originalResizeObserver = globalThis.ResizeObserver;

      class TestResizeObserver {
        constructor(callback: ResizeObserverCallback) {
          resizeObserverCallback = () => callback([], this as unknown as ResizeObserver);
        }

        observe(element: Element): void {
          observedResizeElements.add(element);
        }

        unobserve(element: Element): void {
          observedResizeElements.delete(element);
        }

        disconnect(): void {
          observedResizeElements.clear();
        }
      }

      Object.defineProperty(globalThis, 'ResizeObserver', {
        value: TestResizeObserver,
        configurable: true,
        writable: true,
      });

      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
        get(this: HTMLElement) {
          return isPane(this) ? contentHeight : 0;
        },
        configurable: true,
      });
      Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
        get(this: HTMLElement) {
          return isPane(this) ? PANE_HEIGHT : 0;
        },
        configurable: true,
      });
      Object.defineProperty(HTMLElement.prototype, 'scrollTop', {
        get(this: HTMLElement) {
          return positions.get(this) ?? 0;
        },
        set(this: HTMLElement, value: number) {
          if (isPane(this)) positions.set(this, value);
        },
        configurable: true,
      });
    });

    afterEach(() => {
      for (const property of ['scrollHeight', 'clientHeight', 'scrollTop']) {
        delete (HTMLElement.prototype as unknown as Record<string, unknown>)[property];
      }

      if (originalResizeObserver) {
        Object.defineProperty(globalThis, 'ResizeObserver', {
          value: originalResizeObserver,
          configurable: true,
          writable: true,
        });
      } else {
        delete (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver;
      }
    });

    function body(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-testid="assistant-body"]');
    }

    function readerScrollsTo(position: number): void {
      const element = body();
      element.scrollTop = position;
      element.dispatchEvent(new Event('scroll'));
    }

    async function render(): Promise<void> {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    async function open(): Promise<void> {
      assistant.open();
      await render();
    }

    it('keeps an arriving reply in view when the reader is at the end', async () => {
      await open();

      assistant.updateDraft('What are you reading?');
      assistant.submit();
      fixture.detectChanges();
      http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'Your own library.' }));
      await render();

      expect(body().scrollTop).toBe(END);
    });

    it('follows post-layout transcript growth in compact mode without overriding history reading', async () => {
      await open();
      expect(fixture.componentInstance.expanded()).toBe(false);

      assistant.updateDraft('Keep the compact reply visible.');
      fixture.componentInstance.onSendClick();
      http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'A reply that reflows.' }));
      await render();

      const transcript = fixture.nativeElement.querySelector(
        '[data-testid="assistant-transcript"]',
      ) as HTMLElement;
      expect(transcript).toBeTruthy();
      expect(observedResizeElements.has(transcript)).toBe(true);
      expect(body().scrollTop).toBe(END);

      // Model the compact flyout settling after Angular's render pass. The
      // transcript becomes taller without another signal changing.
      contentHeight += 240;
      resizeObserverCallback?.();

      expect(body().scrollTop).toBe(contentHeight - PANE_HEIGHT);

      readerScrollsTo(200);
      contentHeight += 180;
      resizeObserverCallback?.();

      expect(body().scrollTop).toBe(200);
    });

    it('keeps following when an expanded-shell programmatic scroll event arrives after content grows', async () => {
      await open();
      fixture.componentInstance.toggleExpanded();
      await render();

      assistant.updateDraft('Keep following this reply.');
      fixture.componentInstance.onSendClick();
      await render();

      const element = body();
      expect(element.scrollTop).toBe(END);

      contentHeight = CONTENT_HEIGHT + 240;
      element.dispatchEvent(new Event('scroll'));

      http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'Still visible.' }));
      await render();

      expect(body().scrollTop).toBe(contentHeight - PANE_HEIGHT);
    });

    it('leaves the view alone when a reply arrives while an older turn is being read', async () => {
      await open();
      expect(body().scrollTop).toBe(END);

      assistant.updateDraft('One more thing.');
      fixture.detectChanges();
      fixture.componentInstance.onSendClick();
      await render();

      readerScrollsTo(200);
      await render();

      http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'Noted.' }));
      await render();

      expect(body().scrollTop).toBe(200);
    });

    it('follows again once the reader scrolls back to the end themselves', async () => {
      await open();
      readerScrollsTo(200);
      await render();

      readerScrollsTo(END);
      await render();

      assistant.updateDraft('And now?');
      assistant.submit();
      http.expectOne('/api/assistant/turn/stream').flush(turn({ reply: 'Now this.' }));
      await render();

      expect(body().scrollTop).toBe(END);
    });

    it('returns to the end for the reader own message, wherever they had scrolled', async () => {
      await open();
      readerScrollsTo(0);
      await render();

      assistant.updateDraft('Answer this one.');
      await render();
      fixture.componentInstance.onSendClick();
      await render();

      expect(body().scrollTop).toBe(END);
      http.expectOne('/api/assistant/turn/stream').flush(turn());
    });

    it('lands on the newest turn when the surface is opened', async () => {
      await open();
      readerScrollsTo(0);
      await render();

      fixture.componentInstance.close();
      fixture.detectChanges();
      fixture.componentInstance.open();
      await render();

      expect(body().scrollTop).toBe(END);
    });
  });

  describe('a tall review arrives in view (issue #1557)', () => {
    const PANE_HEIGHT = 400;
    const CONTENT_HEIGHT = 1200;
    const END = CONTENT_HEIGHT - PANE_HEIGHT;
    /** Where the review card starts in the transcript's scroll content. */
    const CARD_TOP = 600;
    let contentHeight = CONTENT_HEIGHT;
    const positions = new WeakMap<Element, number>();
    let originalResizeObserver: typeof ResizeObserver | undefined;
    let originalMatchMedia: typeof window.matchMedia | undefined;
    const originalGetBoundingClientRect = Element.prototype.getBoundingClientRect;

    function isBody(element: Element): boolean {
      return element.getAttribute('data-testid') === 'assistant-body';
    }

    function isCard(element: Element): boolean {
      return element.hasAttribute('data-changeset-id');
    }

    function rect(top: number, width: number, height: number): DOMRect {
      return {
        x: 0, y: top, top, left: 0, right: width, bottom: top + height,
        width, height, toJSON: () => ({}),
      } as DOMRect;
    }

    beforeEach(() => {
      contentHeight = CONTENT_HEIGHT;
      originalResizeObserver = globalThis.ResizeObserver;
      originalMatchMedia = window.matchMedia;

      class TestResizeObserver {
        observe(): void {}
        unobserve(): void {}
        disconnect(): void {}
      }

      Object.defineProperty(globalThis, 'ResizeObserver', {
        value: TestResizeObserver,
        configurable: true,
        writable: true,
      });

      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
        get(this: HTMLElement) {
          return isBody(this) ? contentHeight : 0;
        },
        configurable: true,
      });
      Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
        get(this: HTMLElement) {
          return isBody(this) ? PANE_HEIGHT : 0;
        },
        configurable: true,
      });
      Object.defineProperty(HTMLElement.prototype, 'scrollTop', {
        get(this: HTMLElement) {
          return positions.get(this) ?? 0;
        },
        set(this: HTMLElement, value: number) {
          if (isBody(this)) positions.set(this, value);
        },
        configurable: true,
      });
      Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
        value(this: HTMLElement, options: ScrollToOptions) {
          if (typeof options === 'object' && options.top !== undefined) this.scrollTop = options.top;
        },
        configurable: true,
      });
      Object.defineProperty(Element.prototype, 'getBoundingClientRect', {
        value(this: Element) {
          if (isBody(this)) return rect(0, 335, PANE_HEIGHT);
          if (isCard(this)) {
            const body = this.ownerDocument.querySelector('[data-testid="assistant-body"]');
            const scrolled = body ? positions.get(body) ?? 0 : 0;
            return rect(CARD_TOP - scrolled, 335, 855);
          }
          return rect(0, 0, 0);
        },
        configurable: true,
      });
    });

    afterEach(() => {
      for (const property of ['scrollHeight', 'clientHeight', 'scrollTop', 'scrollTo']) {
        delete (HTMLElement.prototype as unknown as Record<string, unknown>)[property];
      }
      Object.defineProperty(Element.prototype, 'getBoundingClientRect', {
        value: originalGetBoundingClientRect,
        configurable: true,
      });
      if (originalResizeObserver) {
        Object.defineProperty(globalThis, 'ResizeObserver', {
          value: originalResizeObserver,
          configurable: true,
          writable: true,
        });
      } else {
        delete (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver;
      }
      if (originalMatchMedia) window.matchMedia = originalMatchMedia;
      else delete (window as { matchMedia?: unknown }).matchMedia;
    });

    function body(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-testid="assistant-body"]');
    }

    function card(): HTMLElement {
      return fixture.nativeElement.querySelector('[data-changeset-id="cs-9"]');
    }

    function readerScrollsTo(position: number): void {
      const element = body();
      element.scrollTop = position;
      element.dispatchEvent(new Event('scroll'));
    }

    async function render(): Promise<void> {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    async function open(): Promise<void> {
      assistant.open();
      await render();
    }

    async function send(message: string, response: Parameters<typeof turn>[0]): Promise<void> {
      assistant.updateDraft(message);
      fixture.componentInstance.onSendClick();
      await render();
      http.expectOne('/api/assistant/turn/stream').flush(turn(response));
      await render();
    }

    it('puts an arriving review at the top of the transcript, not at its end', async () => {
      await open();
      expect(body().scrollTop).toBe(END);

      await send('Regroup everything.', {
        reply: "Here's what I'd change.",
        changeset: reviewChangeset(),
      });

      expect(card()).toBeTruthy();
      expect(body().scrollTop).toBe(CARD_TOP);
      expect(card().getBoundingClientRect().top).toBe(0);
    });

    it('still follows an ordinary answer to the end', async () => {
      await open();
      await send('What should I read next?', { reply: 'A plain answer.' });

      expect(fixture.nativeElement.querySelector('[data-testid="assistant-changeset"]')).toBeNull();
      expect(body().scrollTop).toBe(END);
    });

    it('does not let a delayed end-of-transcript event re-arm following over an anchor', async () => {
      // Before the review arrives the transcript is only the user's own line,
      // so the last follow target was the top. The card makes it tall.
      contentHeight = PANE_HEIGHT;
      await open();
      expect(body().scrollTop).toBe(0);

      assistant.updateDraft('Regroup everything.');
      fixture.componentInstance.onSendClick();
      await render();
      contentHeight = CONTENT_HEIGHT;
      http.expectOne('/api/assistant/turn/stream').flush(turn({ changeset: reviewChangeset() }));
      await render();
      expect(body().scrollTop).toBe(CARD_TOP);

      // A scroll event from before the anchor lands near the end. It must not
      // switch following back on: the next post-layout render would then snap
      // the transcript down and hide the card again.
      const element = body();
      element.scrollTop = END - 10;
      element.dispatchEvent(new Event('scroll'));
      contentHeight = CONTENT_HEIGHT + 200;
      await render();

      expect(body().scrollTop).toBe(END - 10);
    });

    it('keeps an expanding card header in view', async () => {
      await open();
      await send('Regroup everything.', {
        reply: "Here's what I'd change.",
        changeset: reviewChangeset(),
      });
      readerScrollsTo(CARD_TOP + 120);
      await render();

      const toggle = fixture.nativeElement.querySelector(
        '[data-testid="assistant-changeset-details-toggle"]',
      ) as HTMLButtonElement;
      toggle.click();
      await render();

      expect(card().getBoundingClientRect().top).toBe(0);
      expect(body().scrollTop).toBe(CARD_TOP);
    });

    it('leaves an arriving review alone when an older turn is being read', async () => {
      await open();
      assistant.updateDraft('Regroup everything.');
      fixture.componentInstance.onSendClick();
      await render();
      readerScrollsTo(200);
      await render();

      http.expectOne('/api/assistant/turn/stream').flush(turn({ changeset: reviewChangeset() }));
      await render();

      expect(body().scrollTop).toBe(200);
    });

    it('jumps instead of gliding when the reader asks for reduced motion', async () => {
      const scrollTo = vi.fn(function (this: HTMLElement, options: ScrollToOptions) {
        this.scrollTop = options.top ?? 0;
      });
      Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
        value: scrollTo,
        configurable: true,
      });
      window.matchMedia = ((query: string) =>
        ({ matches: query.includes('prefers-reduced-motion'), media: query })) as typeof window.matchMedia;

      await open();
      await send('Regroup everything.', {
        reply: "Here's what I'd change.",
        changeset: reviewChangeset(),
      });

      expect(scrollTo).not.toHaveBeenCalled();
      expect(body().scrollTop).toBe(CARD_TOP);
    });
  });

  describe('voice capture in the composer', () => {
    function open(): void {
      assistant.open();
      fixture.detectChanges();
    }

    function query(selector: string): any {
      return fixture.nativeElement.querySelector(selector);
    }

    it('shows the mic in the OPEN composer and starts recording on tap', () => {
      open();
      const mic = query('[data-testid="assistant-voice-start"]');
      expect(mic).toBeTruthy();
      expect(query('.assistant-composer').contains(mic)).toBe(true);
      expect(query('.assistant-trigger').contains(mic)).toBe(false);

      mic.click();
      expect(voice.start).toHaveBeenCalledTimes(1);
    });

    it('hides and guards the mic when the voice product preference is off', () => {
      TestBed.inject(LibraryPreferencesService).setAssistantVoiceEnabled(false);
      open();

      expect(query('[data-testid="assistant-voice-start"]')).toBeNull();

      fixture.componentInstance.onMicTap();
      expect(voice.start).not.toHaveBeenCalled();
    });

    it('shows a quiet elapsed timer and a stop control while recording', () => {
      open();
      voice.setElapsed(7);
      voice.setStatus('recording');
      fixture.detectChanges();

      expect(query('[data-testid="assistant-voice-timer"]').textContent).toContain('0:07');
      const stop = query('[data-testid="assistant-voice-stop"]');
      expect(stop).toBeTruthy();
      expect(query('[data-testid="assistant-voice-start"]')).toBeNull();

      stop.click();
      expect(voice.stop).toHaveBeenCalledTimes(1);
    });

    it('shows a transcribing state with a cancel affordance', () => {
      open();
      voice.setStatus('transcribing');
      fixture.detectChanges();

      expect(query('[data-testid="assistant-voice-transcribing"]')).toBeTruthy();
      const cancel = query('[data-testid="assistant-voice-cancel"]');
      expect(cancel).toBeTruthy();

      cancel.click();
      expect(voice.cancel).toHaveBeenCalledTimes(1);
    });

    it('cancels a live recording from the composer', () => {
      open();
      voice.setStatus('recording');
      fixture.detectChanges();

      query('[data-testid="assistant-voice-cancel"]').click();
      expect(voice.cancel).toHaveBeenCalledTimes(1);
    });

    it('does not stick in an error state: message clears, mic returns', () => {
      open();
      voice.setError({
        kind: 'failed',
        message: "Could not transcribe that recording. Try again.",
      });
      fixture.detectChanges();

      const error = query('[data-testid="assistant-voice-error"]');
      expect(error).toBeTruthy();
      expect(error.getAttribute('aria-live')).toBe('polite');
      expect(error.textContent).toContain("Could not transcribe");

      voice.setError(null);
      fixture.detectChanges();
      expect(query('[data-testid="assistant-voice-error"]')).toBeNull();
      expect(query('[data-testid="assistant-voice-start"]')).toBeTruthy();
    });

    it('surfaces a denied-permission message in the surface', () => {
      open();
      voice.setError({
        kind: 'denied',
        message: 'Microphone access is blocked. Allow it in your browser, then try again.',
      });
      fixture.detectChanges();

      expect(query('[data-testid="assistant-voice-error"]').textContent).toContain(
        'Microphone access is blocked',
      );
    });

    it('hands a finished transcript to the conversation, not a second pipeline', () => {
      open();
      expect(voice.onTranscript).toBeTypeOf('function');

      voice.onTranscript?.('The Magic Mountain');

      expect(assistant.draft()).toBe('The Magic Mountain');
      expect(assistant.autoSendPending()).toBe(true);
      http.expectNone('/api/assistant/turn/stream');

      assistant.undoTranscript();
    });

    it('shows the Undo affordance only while the auto-send window is open', () => {
      open();
      expect(query('[data-testid="assistant-voice-undo"]')).toBeNull();

      assistant.insertTranscript('The Magic Mountain');
      fixture.detectChanges();

      const undo = query('[data-testid="assistant-voice-undo"]');
      expect(undo).toBeTruthy();
      expect(undo.textContent).toContain('Undo');
      const undoButton = query('[data-testid="assistant-voice-undo-button"]') as HTMLButtonElement;
      expect(undoButton.classList.contains('nostos-button')).toBe(true);

      undoButton.click();
      fixture.detectChanges();

      expect(assistant.autoSendPending()).toBe(false);
      expect(query('[data-testid="assistant-voice-undo"]')).toBeNull();
      expect(assistant.draft()).toBe('The Magic Mountain');
      http.expectNone('/api/assistant/turn/stream');
    });

    it('closing the surface abandons a live recording', () => {
      open();
      voice.setStatus('recording');
      fixture.detectChanges();

      fixture.componentInstance.close();

      expect(voice.cancel).toHaveBeenCalled();
    });
  });
});
