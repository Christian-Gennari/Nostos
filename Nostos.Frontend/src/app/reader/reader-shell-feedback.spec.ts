import { Component, forwardRef, input, output } from '@angular/core';
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
  sidebarVisible = input(false);
  highlightMode = input(false);
  highlightColour = input<unknown>(null);
  sidebarVisibleChange = output<boolean>();
  noteCreated = output<void>();
  selectionCaptured = output<unknown>();
  commitFailed = output<unknown>();
  surfaceInteracted = output<void>();
}

@Component({ selector: 'app-audio-reader', standalone: true, template: '' })
class AudioReaderStub {
  bookId = input.required<string>();
  book = input<unknown>(null);
}

const pdfBook = {
  id: 'book-1',
  title: 'A PDF book',
  type: 'pdf',
  hasFile: true,
  fileName: 'book-1.pdf',
  lastLocation: null,
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
 * format is set directly so the maximum PDF tool count is exercised without
 * loading a real document.
 */
describe('ReaderShell feedback entry', () => {
  let capabilities: DeploymentCapabilities;
  let viewportWidth: number;

  function mockMatchMedia(): void {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation((query: string) => {
        const max = /\(max-width:\s*(\d+)px\)/.exec(query);
        return {
          matches: max ? viewportWidth <= Number(max[1]) : false,
          media: query,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          addListener: vi.fn(),
          removeListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
      }),
    });
  }

  beforeEach(async () => {
    capabilities = cloudCapabilities;
    viewportWidth = 390;

    const paramMap$ = new BehaviorSubject(convertToParamMap({ id: 'book-1' }));
    const queryParamMap$ = new BehaviorSubject(convertToParamMap({}));

    TestBed.overrideComponent(ReaderShell, {
      remove: { imports: [TopicInputComponent, PdfReader, AudioReader] },
      add: { imports: [TopicInputStub, PdfReaderStub, AudioReaderStub] },
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
          useValue: { list: vi.fn(() => of([])) },
        },
        { provide: TopicsService, useValue: { list: vi.fn(() => of([])) } },
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

  async function render(book: unknown = pdfBook): Promise<ComponentFixture<ReaderShell>> {
    mockMatchMedia();
    const fixture = TestBed.createComponent(ReaderShell);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    // The header (and its feedback utility) renders before the book arrives;
    // give the shell the format so the real tool count is exercised.
    fixture.componentInstance.book.set(book);
    fixture.componentInstance.loading.set(false);
    fixture.componentInstance.loadError.set(null);
    fixture.detectChanges();
    return fixture;
  }

  function openViewSettings(fixture: ComponentFixture<ReaderShell>): void {
    (fixture.nativeElement.querySelector('[data-testid="typo-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  it('renders the header entry with the reader origin on a wide Cloud shell', async () => {
    const fixture = await render();

    const link = fixture.nativeElement.querySelector(
      '[data-testid="reader-feedback"]',
    ) as HTMLAnchorElement;
    expect(link).toBeTruthy();
    expect(link.getAttribute('aria-label')).toBe('Send feedback');
    expect(link.href).toBe('https://nostos.page/feedback?from=reader');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    expect(link.closest('.reader-header-tools')).toBeTruthy();

    openViewSettings(fixture);
    expect(
      fixture.nativeElement.querySelector('[data-testid="reader-feedback-panel"]'),
    ).toBeNull();
  });

  it('moves the entry into View settings below 360px with the full PDF tool count', async () => {
    viewportWidth = 320;
    const fixture = await render();

    expect(fixture.nativeElement.querySelectorAll('.reader-header-tools .icon-btn').length).toBe(4);
    expect(fixture.nativeElement.querySelector('[data-testid="reader-feedback"]')).toBeNull();

    openViewSettings(fixture);
    const panelLink = fixture.nativeElement.querySelector(
      '[data-testid="reader-feedback-panel"]',
    ) as HTMLAnchorElement;
    expect(panelLink).toBeTruthy();
    expect(panelLink.href).toBe('https://nostos.page/feedback?from=reader');
    expect(panelLink.target).toBe('_blank');
    expect(panelLink.rel).toBe('noopener noreferrer');
  });

  it('keeps the header entry for audio, which has no View settings panel', async () => {
    viewportWidth = 320;
    const fixture = await render(audioBook);

    expect(
      fixture.nativeElement.querySelector('[data-testid="reader-feedback"]'),
    ).toBeTruthy();
    expect(
      fixture.nativeElement.querySelector('[data-testid="reader-feedback-panel"]'),
    ).toBeNull();
  });

  it('omits both entries on SelfHosted at every width', async () => {
    capabilities = selfHostedCapabilities;
    viewportWidth = 320;
    const fixture = await render();

    expect(fixture.nativeElement.querySelector('[data-testid="reader-feedback"]')).toBeNull();
    openViewSettings(fixture);
    expect(
      fixture.nativeElement.querySelector('[data-testid="reader-feedback-panel"]'),
    ).toBeNull();
  });
});
