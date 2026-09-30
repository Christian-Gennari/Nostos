import { ComponentFixture, TestBed } from '@angular/core/testing';
import { computed, signal } from '@angular/core';
import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';

import { AssistantComponent } from './assistant.component';
import {
  ASSISTANT_PENDING_DELAY_MS,
  ASSISTANT_SESSION_STORAGE_KEY,
  AssistantEntry,
  AssistantEvidenceReferenceDto,
  AssistantService,
  AssistantSourceReferenceDto,
  AssistantTurnArtifact,
  AssistantTurnResponse,
} from './assistant.service';
import { AssistantStatusService } from './assistant-status.service';
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
    concept: null,
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
    pendingPlan: null,
    capturedNoteId: null,
    ...overrides,
  };
}

describe('AssistantComponent (Cmd/Ctrl+J)', () => {
  let fixture: ComponentFixture<AssistantComponent>;
  let assistant: AssistantService;
  let http: HttpTestingController;
  let fake: ReturnType<typeof fakeContextService>;
  let voice: ReturnType<typeof fakeVoiceService>;
  let status: ReturnType<typeof fakeStatusService>;
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
    router = { navigate: vi.fn(() => Promise.resolve(true)) };

    await TestBed.configureTestingModule({
      imports: [AssistantComponent],
      providers: [
        { provide: AssistantContextService, useValue: fake },
        { provide: AssistantVoiceService, useValue: voice },
        { provide: AssistantStatusService, useValue: status },
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

  it('opens note and concept evidence on canonical Brain deep-links', () => {
    const note: AssistantEvidenceReferenceDto = {
      handle: { kind: 'note', noteId: 'note-42' },
      label: 'Note · The Magic Mountain',
      excerpt: 'A canonical note.',
    };
    const concept: AssistantEvidenceReferenceDto = {
      handle: { kind: 'concept', conceptId: 'concept-7' },
      label: 'Attention',
      excerpt: 'Canonical concept evidence.',
    };

    expect(fixture.componentInstance.canOpenEvidence(note)).toBe(true);
    fixture.componentInstance.openEvidence(note);
    expect(router.navigate).toHaveBeenCalledWith(
      ['/second-brain'],
      { queryParams: { noteId: 'note-42' } },
    );

    expect(fixture.componentInstance.canOpenEvidence(concept)).toBe(true);
    fixture.componentInstance.openEvidence(concept);
    expect(router.navigate).toHaveBeenCalledWith(
      ['/second-brain'],
      { queryParams: { conceptId: 'concept-7' } },
    );
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

      expect(router.navigate).toHaveBeenCalledWith(
        ['/second-brain'],
        { queryParams: { noteId: 'note-grounded' } },
      );
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
      const hidden = indicator.querySelector('.visually-hidden');
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
  it('shows the raw transcript of a captured note and restores it', () => {
    assistant.open();
    assistant.updateDraft('so anyway i was thinking');
    assistant.submit();

    http
      .expectOne('/api/assistant/turn/stream')
      .flush(turn({ acknowledgement: 'Saved.', capturedNoteId: 'note-9' }));
    fixture.detectChanges();

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="assistant-raw-toggle"]',
    ) as HTMLButtonElement;
    expect(toggle).toBeTruthy();
    expect(toggle.classList.contains('nostos-button')).toBe(true);
    toggle.click();
    fixture.detectChanges();

    const raw = http.expectOne('/api/notes/note-9/raw');
    expect(raw.request.method).toBe('GET');
    raw.flush({
      id: 'note-9',
      rawContent: 'so anyway i was thinking',
      content: 'I was thinking.',
      processingMode: 'light_polish',
    });
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-raw-text"]').textContent,
    ).toContain('so anyway i was thinking');
    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-raw-mode"]').textContent,
    ).toContain('light_polish');

    const restoreButton = fixture.nativeElement.querySelector(
      '[data-testid="assistant-raw-restore"]',
    ) as HTMLButtonElement;
    expect(restoreButton.classList.contains('nostos-button')).toBe(true);
    restoreButton.click();
    fixture.detectChanges();

    const restore = http.expectOne('/api/notes/note-9/raw/restore');
    expect(restore.request.method).toBe('POST');
    restore.flush({
      id: 'note-9',
      rawContent: 'so anyway i was thinking',
      content: 'so anyway i was thinking',
      processingMode: 'verbatim',
    });
    fixture.detectChanges();

    expect(assistant.rawTranscript()?.processingMode).toBe('verbatim');
  });

  it('says plainly when a captured note kept no separate original text', () => {
    assistant.open();
    assistant.updateDraft('The snow was general all over Ireland.');
    assistant.submit();

    http
      .expectOne('/api/assistant/turn/stream')
      .flush(turn({ acknowledgement: 'Saved.', capturedNoteId: 'note-quote' }));
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector(
        '[data-testid="assistant-raw-toggle"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    http.expectOne('/api/notes/note-quote/raw').flush({
      id: 'note-quote',
      rawContent: null,
      content: '',
      processingMode: 'verbatim',
    });
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="assistant-raw-empty"]').textContent,
    ).toContain('no separate original');
  });

  it('offers no raw transcript when the turn captured nothing', () => {
    assistant.open();
    assistant.updateDraft('Where does this go?');
    assistant.submit();

    http.expectOne('/api/assistant/turn/stream').flush(turn());
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-raw"]')).toBeNull();
  });

  it('renders suggestion chips and links the chosen existing concept without a second approval', () => {
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
        { kind: 'concept', label: 'Mountains', reason: 'Evidence relationship.', value: 'c-alpha', noteId: 'note-1' },
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
    request.flush(turn({ reply: 'Linked the note to Mountains.', pendingPlan: null }));
    fixture.detectChanges();

    expect(assistant.pendingPlan()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-plan"]')).toBeNull();
  });

  it('uses the plan surface only as a lightweight destructive confirmation fallback', () => {
    assistant.open();
    assistant.pendingPlan.set({
      planId: 'plan-delete',
      summary: 'Delete the obsolete collection. Its books will stay in the library.',
      steps: [
        {
          capability: 'library_delete_collection',
          summary: 'Delete the obsolete collection',
          argumentsJson: '{}',
        },
      ],
      approvalToken: 'token-delete',
    });
    fixture.detectChanges();

    const plan = fixture.nativeElement.querySelector('[data-testid="assistant-plan"]');
    expect(plan).toBeTruthy();
    expect(plan.textContent).toContain('Confirmation required');
    expect(plan.textContent).toContain('Confirm change');
    expect(plan.textContent).toContain('reply “yes” or “go ahead”');

    const approve = plan.querySelector(
      '[data-testid="assistant-plan-approve"]',
    ) as HTMLButtonElement;
    expect(approve.classList.contains('nostos-button')).toBe(true);
    expect(approve.classList.contains('nostos-button--primary')).toBe(true);
    approve.click();
    fixture.detectChanges();

    const approval = http.expectOne('/api/assistant/plan/approve');
    expect(approval.request.body).toEqual({
      planId: 'plan-delete',
      approvalToken: 'token-delete',
    });
    approval.flush({
      success: true,
      errorCode: null,
      errorMessage: null,
      steps: [
        {
          capability: 'library_delete_collection',
          success: true,
          errorCode: null,
          errorMessage: null,
          data: { reply: 'Deleted the obsolete collection.' },
        },
      ],
    });
    fixture.detectChanges();

    expect(assistant.pendingPlan()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-plan"]')).toBeNull();
  });

  it('leaves the note unlinked when the user dismisses the suggestions', () => {
    assistant.open();
    assistant.updateDraft('Suggest a concept');
    assistant.submit();
    http.expectOne('/api/assistant/turn/stream').flush(turn({
      suggestions: [
        { kind: 'concept', label: 'Mountains', reason: 'Evidence relationship.', value: 'c-alpha', noteId: 'note-1' },
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
        message: "Couldn't transcribe that recording. Try again.",
      });
      fixture.detectChanges();

      const error = query('[data-testid="assistant-voice-error"]');
      expect(error).toBeTruthy();
      expect(error.getAttribute('aria-live')).toBe('polite');
      expect(error.textContent).toContain("Couldn't transcribe");

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
