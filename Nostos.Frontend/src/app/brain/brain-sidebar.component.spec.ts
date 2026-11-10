import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { BrainSidebarComponent } from './brain-sidebar.component';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';
import type { TopicDto } from '../core/services/topics.service';

const topics: TopicDto[] = [
  {
    id: 't1',
    name: 'Alpha',
    usageCount: 3,
    noteMatchCount: 0,
    noteMatchSnippet: null,
    description: null,
  },
  {
    id: 't2',
    name: 'Beta',
    usageCount: 1,
    noteMatchCount: 0,
    noteMatchSnippet: null,
    description: null,
  },
];

describe('BrainSidebarComponent', () => {
  let fixture: ComponentFixture<BrainSidebarComponent>;
  let startRename: ReturnType<typeof vi.fn>;
  let openDelete: ReturnType<typeof vi.fn>;

  function closeMenus(): void {
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  }

  /** Opens a topic row's touch menu and returns the items now in the overlay. */
  function rowMenuItems(name: string): HTMLButtonElement[] {
    const trigger = fixture.nativeElement.querySelector(
      `[aria-label="Actions for ${name}"]`,
    ) as HTMLButtonElement;
    trigger.click();
    fixture.detectChanges();
    return Array.from(document.querySelectorAll<HTMLButtonElement>('.nostos-menu-item'));
  }

  beforeEach(async () => {
    startRename = vi.fn();
    openDelete = vi.fn();

    await TestBed.configureTestingModule({
      imports: [BrainSidebarComponent],
      providers: [
        provideRouter([]),
        {
          provide: BrainStateService,
          useValue: {
            summary: signal({ total: 4, passages: 1, unsorted: 3 }),
            sidebarTopics: signal(topics),
            topicsLoaded: signal(true),
            topicsFailed: signal(false),
            topicSort: signal('usage'),
            refresh: vi.fn(),
            setTopicSort: vi.fn(),
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
            startRename,
            commitRename: vi.fn(),
            cancelRename: vi.fn(),
            openDelete,
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainSidebarComponent);
    fixture.detectChanges();
  });

  afterEach(closeMenus);

  it('gives every topic row its own touch menu trigger', () => {
    expect(
      fixture.nativeElement.querySelectorAll('.topic-row-shell .row-menu-trigger'),
    ).toHaveLength(2);
  });

  it('opens Rename and Delete from the row menu', () => {
    const items = rowMenuItems('Alpha');

    expect(items.map((item) => item.textContent?.trim())).toEqual(['Rename', 'Delete']);

    items.find((item) => item.textContent?.trim() === 'Rename')!.click();
    expect(startRename).toHaveBeenCalledWith(expect.objectContaining({ id: 't1' }), 'sidebar');

    rowMenuItems('Alpha').find((item) => item.textContent?.trim() === 'Delete')!.click();
    expect(openDelete).toHaveBeenCalledWith(expect.objectContaining({ id: 't1' }));
  });

  it('keeps the count visible in the row beside the menu', () => {
    const row = fixture.nativeElement.querySelector('.topic-row-shell') as HTMLElement;

    expect(row.querySelector('.count')?.textContent?.trim()).toBe('3');
    expect(row.querySelector('.row-menu-trigger')).toBeTruthy();
  });
});
