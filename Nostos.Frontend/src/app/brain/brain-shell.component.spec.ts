import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainShellComponent } from './brain-shell.component';
import { BrainStateService } from './brain-state.service';

describe('BrainShellComponent', () => {
  let fixture: ComponentFixture<BrainShellComponent>;
  let preferences: { sidebarExpanded: ReturnType<typeof signal<boolean>>; setSidebarExpanded: (value: boolean) => void };
  let drawerOpen: ReturnType<typeof signal<boolean>>;

  beforeEach(async () => {
    drawerOpen = signal(false);
    preferences = {
      sidebarExpanded: signal(true),
      setSidebarExpanded: (value: boolean) => preferences.sidebarExpanded.set(value),
    };

    await TestBed.configureTestingModule({
      imports: [BrainShellComponent],
      providers: [
        provideRouter([]),
        { provide: LibraryPreferencesService, useValue: preferences },
        {
          provide: BrainStateService,
          useValue: {
            drawerOpen,
            summary: signal(null),
            sidebarTopics: signal([]),
            topicsLoaded: signal(true),
            topicsFailed: signal(false),
            topicSort: signal('usage'),
            topics: signal([]),
            lookup: signal(new Map()),
            topicById: () => null,
            refresh: vi.fn(),
            setTopicSort: vi.fn(),
          },
        },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainShellComponent);
    fixture.detectChanges();
  });

  function layout(): HTMLElement {
    return fixture.nativeElement.querySelector('.brain-layout') as HTMLElement;
  }

  it('opens with the sidebar, like the Library', () => {
    expect(preferences.sidebarExpanded()).toBe(true);
    expect(layout().classList).not.toContain('sidebar-collapsed');
  });

  it('collapses the sidebar to the Library’s rail from the one preference', () => {
    preferences.sidebarExpanded.set(false);
    fixture.detectChanges();

    const column = fixture.nativeElement.querySelector('.brain-sidebar-column') as HTMLElement;

    expect(layout().classList).toContain('sidebar-collapsed');
    // The column stays in the layout: the 300px panel inside it is clipped to
    // the 72px rail, not removed.
    expect(column).toBeTruthy();
    expect(column.querySelector('.sidebar')?.classList).toContain('collapsed');
  });

  it('keeps the phone drawer independent of the desktop preference', () => {
    preferences.sidebarExpanded.set(false);
    drawerOpen.set(true);
    fixture.detectChanges();

    expect(layout().classList).toContain('sidebar-collapsed');
    expect(layout().classList).toContain('drawer-open');
  });
});
