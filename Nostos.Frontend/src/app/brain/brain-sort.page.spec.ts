import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { AssistantService } from '../ui/assistant/assistant.service';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainSortPage } from './brain-sort.page';
import { BrainSortPositionService } from './brain-sort-position.service';
import { BrainStateService } from './brain-state.service';

describe('BrainSortPage when the notes cannot be loaded', () => {
  let fixture: ComponentFixture<BrainSortPage>;
  let browse: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    browse = vi.fn().mockReturnValue(throwError(() => new Error('offline')));
    await TestBed.configureTestingModule({
      imports: [BrainSortPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: NotesService, useValue: { browse } },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: BrainSortPositionService, useValue: { read: () => of(null), write: vi.fn(), clear: vi.fn() } },
        { provide: AssistantService, useValue: { surfaceAvailable: signal(false), turnFinished: new Subject() } },
        {
          provide: BrainStateService,
          useValue: { ensureLoaded: vi.fn(), refresh: vi.fn(), topics: signal([]), lookup: signal(new Map()) },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(BrainSortPage);
    fixture.detectChanges();
  });

  it('says so, with Try again, and loads the first page again from there', () => {
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('The notes could not be loaded.');

    browse.mockReturnValue(of({ items: [], totalCount: 0, offset: 0, limit: 50 }));
    (el.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(browse).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[role="alert"]')).toBeNull();
    expect(el.textContent).toContain('All sorted');
  });
});
