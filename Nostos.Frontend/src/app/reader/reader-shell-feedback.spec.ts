import { Component, forwardRef, input, output } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';

import { ReaderShell } from './reader-shell.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { TopicsService } from '../core/services/topics.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../core/dtos/deployment-capabilities.dtos';

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
 * The Reader is its own shell, so this spec renders the real ReaderShell with a
 * book that fails to load: the header (and its feedback utility) renders while
 * the heavy readers never instantiate. That is also the state where reporting a
 * problem matters most.
 */
describe('ReaderShell feedback entry', () => {
  let capabilities: DeploymentCapabilities;

  beforeEach(async () => {
    capabilities = cloudCapabilities;

    const paramMap$ = new BehaviorSubject(convertToParamMap({ id: 'book-1' }));
    const queryParamMap$ = new BehaviorSubject(convertToParamMap({}));

    TestBed.overrideComponent(ReaderShell, {
      remove: { imports: [TopicInputComponent] },
      add: { imports: [TopicInputStub] },
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
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(capabilities) },
        },
      ],
    }).compileComponents();

    await TestBed.inject(Router).navigateByUrl('/read/book-1');
  });

  async function render(): Promise<ComponentFixture<ReaderShell>> {
    const fixture = TestBed.createComponent(ReaderShell);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('renders a Cloud feedback link carrying only the reader origin', async () => {
    const fixture = await render();

    const link = fixture.nativeElement.querySelector(
      '[data-testid="reader-feedback"]',
    ) as HTMLAnchorElement;
    expect(link).toBeTruthy();
    expect(link.getAttribute('aria-label')).toBe('Send feedback');
    expect(link.href).toBe('https://nostos.page/feedback?from=reader');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');

    // The entry lives in the reader header's utility strip, above the page.
    expect(link.closest('.reader-header-tools')).toBeTruthy();
  });

  it('omits the feedback entry on SelfHosted', async () => {
    capabilities = selfHostedCapabilities;

    const fixture = await render();

    expect(
      fixture.nativeElement.querySelector('[data-testid="reader-feedback"]'),
    ).toBeNull();
  });
});
