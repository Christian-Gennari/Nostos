import { Component, forwardRef, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';

import { ReaderShell } from './reader-shell.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { PdfReader } from './pdf-reader/pdf-reader.component';
import { AudioReader } from './audio-reader/audio-reader.component';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService } from '../core/services/topics.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../core/dtos/deployment-capabilities.dtos';
import { AssistantService } from '../ui/assistant/assistant.service';
import { BrainStateService } from '../brain/brain-state.service';
import { BrainTopicSuggestionsService } from '../brain/brain-topic-suggestions.service';

@Component({ standalone: true, template: '' })
class BlankComponent {}

/** The quick-note field is always in the DOM; the real one needs ngModel wiring. */
@Component({
  selector: 'app-topic-input',
  standalone: true,
  template: '',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => TopicInputStub),
      multi: true,
    },
  ],
})
class TopicInputStub implements ControlValueAccessor {
  placeholder = input('');
  rows = input(1);
  submitTrigger = output<void>();
  writeValue(): void {}
  registerOnChange(): void {}
  registerOnTouched(): void {}
}

@Component({ selector: 'app-pdf-reader', standalone: true, template: '' })
class PdfReaderStub {
  bookId = input.required<string>();
  initialLocation = input<string | null>(null);
  language = input<string | null>(null);
  sidebarVisible = input(false);
  highlightColour = input<unknown>(null);
  sidebarVisibleChange = output<boolean>();
  noteCreated = output<void>();
  selectionCaptured = output<unknown>();
  selectionAnchored = output<unknown>();
  selectionDismissed = output<void>();
  commitFailed = output<unknown>();
  surfaceInteracted = output<void>();
}

@Component({ selector: 'app-audio-reader', standalone: true, template: '' })
class AudioReaderStub {
  bookId = input.required<string>();
  book = input<unknown>(null);
  contentsRequested = output<void>();
}

const pdfBook = {
  id: 'book-1',
  title: 'A PDF book',
  type: 'pdf',
  hasFile: true,
  fileName: 'book-1.pdf',
  lastLocation: null,
};

const pdfWithAudiobook = {
  ...pdfBook,
  otherEditions: [{ id: 'book-2', type: 'audiobook', progressPercent: 0, hasFile: true }],
};

const audioBook = {
  id: 'book-1',
  title: 'An audio book',
  type: 'audio',
  hasFile: true,
  fileName: 'book-1.m4b',
  lastLocation: null,
};

const cloudCapabilities: DeploymentCapabilities = {
  deploymentMode: 'Cloud',
  requiresAuthentication: true,
  canConfigureAiProvider: false,
  managedAi: true,
  managedVoiceTranscription: true,
  usesCloudStorage: true,
  supportsLocalBackupConfiguration: false,
  supportsPrivateNetworkAccess: false,
  supportsEreaderAccess: true,
  usageMeteringAvailable: true,
  accountManagementUrl: 'https://nostos.page/account',
  feedbackUrl: 'https://nostos.page/feedback?from=settings',
};

const selfHostedCapabilities: DeploymentCapabilities = {
  ...cloudCapabilities,
  deploymentMode: 'SelfHosted',
  managedAi: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  usageMeteringAvailable: false,
  accountManagementUrl: null,
  feedbackUrl: null,
};

/**
 * The Reader is its own shell, so this spec renders the real ReaderShell. The
 * book request fails first (that state also renders the header), then the
 * format is set directly so the header is exercised without loading a real
 * document.
 *
 * The contract: the header holds the same four tools at every width —
 * Contents, Notes, View settings (EPUB/PDF) and More — and More holds the
 * once-a-session tools: search, read aloud, another edition, feedback.
 */
describe('ReaderShell header tools', () => {
  let capabilities: DeploymentCapabilities;

  beforeEach(async () => {
    capabilities = cloudCapabilities;

    const paramMap$ = new BehaviorSubject(convertToParamMap({ id: 'book-1' }));
    const queryParamMap$ = new BehaviorSubject(convertToParamMap({}));

    TestBed.overrideComponent(ReaderShell, {
      remove: { imports: [TopicInputComponent, PdfReader, AudioReader], providers: [BrainTopicSuggestionsService] },
      add: {
        imports: [TopicInputStub, PdfReaderStub, AudioReaderStub],
        // The notes panel's topic suggestions talk to Ask Nostos; not under test here.
        providers: [{ provide: BrainTopicSuggestionsService, useValue: { namesFor: () => [], stateFor: () => 'off' } }],
      },
    });

    await TestBed.configureTestingModule({
      imports: [ReaderShell],
      providers: [
        provideRouter([{ path: 'read/:id', component: BlankComponent }]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ id: 'book-1' }),
              queryParamMap: convertToParamMap({}),
            },
            paramMap: paramMap$.asObservable(),
            queryParamMap: queryParamMap$.asObservable(),
          },
        },
        {
          provide: BooksService,
          useValue: {
            get: vi.fn(() => throwError(() => new Error('boom'))),
            updateProgress: vi.fn(() => of(null)),
          },
        },
        {
          provide: NotesService,
          useValue: { list: vi.fn(() => of([])), browse: vi.fn(() => of({ items: [], totalCount: 0 })) },
        },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])) } },
        {
          provide: BrainStateService,
          useValue: { topics: signal([]), lookup: signal(new Map()), ensureLoaded: vi.fn(), refresh: vi.fn() },
        },
        { provide: TopicAutocompleteService, useValue: { setTopics: vi.fn() } },
        {
          provide: AssistantService,
          useValue: {
            requestSurfaceOpen: vi.fn(),
            isOpen: vi.fn(() => false),
            surfaceAvailable: vi.fn(() => true),
          },
        },
        {
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(capabilities) },
        },
      ],
    }).compileComponents();

    await TestBed.inject(Router).navigateByUrl('/read/book-1');
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
    vi.restoreAllMocks();
  });

  async function render(
    book: unknown = pdfBook,
    { loadError = null as string | null } = {},
  ): Promise<ComponentFixture<ReaderShell>> {
    const fixture = TestBed.createComponent(ReaderShell);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    fixture.componentInstance.book.set(book);
    fixture.componentInstance.loading.set(false);
    fixture.componentInstance.loadError.set(loadError);
    fixture.detectChanges();
    return fixture;
  }

  function headerTools(fixture: ComponentFixture<ReaderShell>): string[] {
    return Array.from(
      fixture.nativeElement.querySelectorAll('.reader-header-tools > button'),
      (button) => (button as HTMLElement).getAttribute('aria-label') ?? '',
    );
  }

  function moreButton(fixture: ComponentFixture<ReaderShell>): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('[data-testid="reader-more"]');
  }

  /** Opens More and returns the labels of its items, in order. */
  function openMore(fixture: ComponentFixture<ReaderShell>): string[] {
    moreButton(fixture)!.click();
    fixture.detectChanges();
    return Array.from(
      document.querySelectorAll('[data-testid="reader-more-menu"] .nostos-menu-item__label'),
      (label) => label.textContent?.trim() ?? '',
    );
  }

  function moreItem(testId: string): HTMLButtonElement | null {
    return document.querySelector(`[data-testid="reader-more-menu"] [data-testid="${testId}"]`);
  }

  it('holds Contents, Notes, View settings and More for a PDF', async () => {
    const fixture = await render(pdfWithAudiobook);

    expect(headerTools(fixture)).toEqual([
      'Table of contents',
      'Notes & highlights',
      'View settings',
      'More reading tools',
    ]);
    // Nothing that used to sit in the header is still there.
    for (const id of ['reader-search-toggle', 'read-aloud-start', 'reader-switch-edition', 'reader-feedback']) {
      expect(fixture.nativeElement.querySelector(`[data-testid="${id}"]`)).toBeNull();
    }
  });

  it('puts search, the other edition and feedback in More, grouped', async () => {
    const fixture = await render(pdfWithAudiobook);

    expect(openMore(fixture)).toEqual([
      'Search in book',
      'Continue in the audiobook',
      'Send feedback',
    ]);
    expect(document.querySelectorAll('[data-testid="reader-more-menu"] hr').length).toBe(2);
    expect(moreItem('reader-switch-edition')?.textContent).toContain('Picks up where you are');
  });

  it('keeps search visible but disabled until the reader can search', async () => {
    const fixture = await render();
    openMore(fixture);

    const search = moreItem('reader-search-toggle')!;
    expect(search.getAttribute('aria-disabled')).toBe('true');
    expect(search.textContent).toContain('Available once the book has opened');
  });

  it('opens feedback in a new tab with the reader origin', async () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    const fixture = await render();
    openMore(fixture);

    moreItem('reader-feedback')!.click();
    fixture.detectChanges();

    expect(open).toHaveBeenCalledWith(
      'https://nostos.page/feedback?from=reader',
      '_blank',
      'noopener,noreferrer',
    );
  });

  it('opens the edition dialog from More and closes the menu', async () => {
    const fixture = await render(pdfWithAudiobook);
    const shell = fixture.componentInstance;
    openMore(fixture);

    moreItem('reader-switch-edition')!.click();
    // A PDF reads its page text before the dialog opens.
    await fixture.whenStable();
    fixture.detectChanges();

    expect(shell.switchEditionOpen()).toBe(true);
    expect(shell.moreOpen()).toBe(false);
  });

  it('keeps chrome up while More is open, and a page tap only closes it', async () => {
    const fixture = await render();
    const shell = fixture.componentInstance;
    openMore(fixture);

    expect(shell.moreOpen()).toBe(true);
    expect(shell.chromeShown()).toBe(true);

    shell.handleSurfaceInteraction();
    fixture.detectChanges();

    expect(shell.moreOpen()).toBe(false);
    expect(shell.chromeVisible()).toBe(false);
  });

  it('gives audio Contents, Notes and More, with no reading-only items', async () => {
    const fixture = await render(audioBook);

    expect(headerTools(fixture)).toEqual(['Table of contents', 'Notes & highlights', 'More reading tools']);
    expect(openMore(fixture)).toEqual(['Send feedback']);
    expect(document.querySelectorAll('[data-testid="reader-more-menu"] hr').length).toBe(0);
  });

  it('still offers feedback when the book fails to load', async () => {
    const fixture = await render(pdfBook, { loadError: 'Could not open' });

    expect(headerTools(fixture)).toEqual(['More reading tools']);
    expect(openMore(fixture)).toEqual(['Send feedback']);
  });

  it('names the edition only when there is one to open', async () => {
    const fixture = await render({
      ...pdfBook,
      otherEditions: [
        { id: 'book-2', type: 'audiobook', progressPercent: 0, hasFile: true },
        { id: 'book-3', type: 'audiobook', progressPercent: 0, hasFile: true },
      ],
    });

    expect(openMore(fixture)).toContain('Continue in another edition');
  });

  it('omits feedback on SelfHosted, and More with it when nothing else is left', async () => {
    capabilities = selfHostedCapabilities;
    const pdf = await render();
    expect(openMore(pdf)).toEqual(['Search in book']);
    pdf.destroy();

    const audio = await render(audioBook);
    expect(headerTools(audio)).toEqual(['Table of contents', 'Notes & highlights']);
    expect(moreButton(audio)).toBeNull();
  });
});
