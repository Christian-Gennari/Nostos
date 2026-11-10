import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { BrainListCacheService } from './brain-list-cache.service';
import { BrainListPage } from './brain-list.page';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';

describe('BrainListPage', () => {
  let fixture: ComponentFixture<BrainListPage>;
  let preferences: { sidebarExpanded: ReturnType<typeof signal<boolean>>; setSidebarExpanded: (value: boolean) => void };

  beforeAll(() => {
    // jsdom has no scrolling; the page resets the scroller when a list opens.
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
      configurable: true,
      writable: true,
      value: vi.fn(),
    });
  });

  beforeEach(async () => {
    preferences = {
      sidebarExpanded: signal(true),
      setSidebarExpanded: (value: boolean) => preferences.sidebarExpanded.set(value),
    };

    await TestBed.configureTestingModule({
      imports: [BrainListPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            data: of({ place: 'all' }),
            paramMap: of(convertToParamMap({})),
            queryParamMap: of(convertToParamMap({})),
            snapshot: {
              data: { place: 'all' },
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap({}),
            },
          },
        },
        {
          provide: NotesService,
          useValue: {
            browse: vi.fn(() => of({ items: [], totalCount: 0, offset: 0, limit: 25 })),
            books: vi.fn(() => of([])),
          },
        },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: BrainListCacheService, useValue: { restore: () => null, save: vi.fn() } },
        { provide: LibraryPreferencesService, useValue: preferences },
        { provide: AssistantService, useValue: { turnFinished: new Subject() } },
        { provide: AssistantContextService, useValue: {} },
        {
          provide: BrainStateService,
          useValue: {
            ensureLoaded: vi.fn(),
            topicById: () => null,
            topics: signal([]),
            lookup: signal(new Map()),
            drawerOpen: signal(false),
            dataVersion: signal(0),
            topicsRewritten: signal(0),
          },
        },
        {
          provide: BrainTopicActionsService,
          useValue: {
            renameId: signal<string | null>(null),
            renameSurface: signal('sidebar'),
            renameValue: signal(''),
            renameError: signal<string | null>(null),
            renaming: signal(false),
            startRename: vi.fn(),
            commitRename: vi.fn(),
            cancelRename: vi.fn(),
            openDelete: vi.fn(),
            openMerge: vi.fn(),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainListPage);
    fixture.detectChanges();
    fixture.detectChanges();
  });

  it('offers the Library’s sidebar toggle and flips the one preference', () => {
    const toggle = fixture.nativeElement.querySelector('button.sidebar-toggle') as HTMLButtonElement;

    expect(toggle).toBeTruthy();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-label')).toBe('Collapse sidebar');

    toggle.click();
    fixture.detectChanges();

    expect(preferences.sidebarExpanded()).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-label')).toBe('Expand sidebar');
  });
});
