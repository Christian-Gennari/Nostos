import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OverlayModule } from '@angular/cdk/overlay';
import { ActivatedRoute, convertToParamMap, provideRouter, Router, type ParamMap } from '@angular/router';
import { BehaviorSubject, of, Subject } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { NotesService } from '../../core/services/notes.service';
import { ToastService } from '../../core/services/toast.service';
import { TopicsService, type TopicDto, type TopicKindDto } from '../../core/services/topics.service';
import { AssistantService } from '../../ui/assistant/assistant.service';
import { BrainKindsPage } from './brain-kinds.page';
import { BrainStateService } from '../brain-state.service';

const kind = (id: string, name: string, colorKey: string, sortOrder: number, topicCount = 0): TopicKindDto => ({
  id,
  name,
  colorKey,
  sortOrder,
  createdAt: '2026-01-01T00:00:00Z',
  topicCount,
});

const person = kind('k1', 'Person', 'kind-1', 1, 34);
const idea = kind('k2', 'Idea', 'kind-2', 2);

const topic = (id: string, name: string, kindId: string | null = null, source: TopicDto['kindSource'] = null): TopicDto => ({
  id,
  name,
  usageCount: 3,
  kindId,
  kindSource: source,
});

describe('BrainKindsPage', () => {
  let fixture: ComponentFixture<BrainKindsPage>;
  let query: BehaviorSubject<ParamMap>;
  let kinds: TopicKindDto[];
  let topics: TopicDto[];
  let setTopicsKind: ReturnType<typeof vi.fn>;
  let updateKind: ReturnType<typeof vi.fn>;
  let createKind: ReturnType<typeof vi.fn>;
  let deleteKind: ReturnType<typeof vi.fn>;

  const component = (): BrainKindsPage => fixture.componentInstance;

  beforeEach(async () => {
    query = new BehaviorSubject<ParamMap>(convertToParamMap({}));
    topics = [
      topic('t1', 'Ursula K. Le Guin'),
      topic('t2', 'Education', 'k1', 'assistant'),
      topic('t3', 'Tolkien'),
      topic('t4', 'Story', 'k2', 'connected-app'),
    ];
    kinds = [person, idea];
    setTopicsKind = vi.fn((ids: string[], kindId: string | null) =>
      of({ updatedTopicIds: ids, kindId, kindSource: kindId ? 'reader' : null }));
    updateKind = vi.fn(() => of(person));
    createKind = vi.fn(() => of(kind('k3', 'Field', 'kind-3', 3)));
    deleteKind = vi.fn(() => of(undefined));

    await TestBed.configureTestingModule({
      imports: [BrainKindsPage, OverlayModule],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: query.asObservable(),
            snapshot: { queryParamMap: convertToParamMap({}) },
          },
        },
        {
          provide: TopicsService,
          useValue: {
            list: () => of(topics),
            listKinds: () => of(kinds),
            setTopicsKind,
            updateKind,
            createKind,
            deleteKind,
          },
        },
        {
          provide: BooksService,
          useValue: {
            list: () => of({
              items: [
                { author: 'Ursula K. Le Guin' },
                { author: 'J.R.R. Tolkien' },
              ],
              totalCount: 2,
              page: 1,
              pageSize: 500,
            }),
          },
        },
        { provide: NotesService, useValue: { summary: () => of({ total: 0 }) } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainKindsPage);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    document.querySelectorAll('.cdk-overlay-container').forEach((node) => node.remove());
  });

  function setQuery(params: Record<string, string>): void {
    query.next(convertToParamMap(params));
    fixture.detectChanges();
  }

  function checkbox(index: number, desired: boolean, shift = false): void {
    const target = document.createElement('input');
    target.checked = desired;
    const event = new MouseEvent('click', { shiftKey: shift });
    Object.defineProperty(event, 'target', { value: target });
    component().onCheckboxClick(index, event);
  }

  function indexOf(id: string): number {
    return component().topics().findIndex((item) => item.id === id);
  }

  it('filters by untyped, a kind, Ask Nostos and a connected app, all from the URL', () => {
    setQuery({ filter: 'untyped' });
    expect(component().topics().map((t) => t.id).sort()).toEqual(['t1', 't3']);

    setQuery({ filter: 'k2' });
    expect(component().topics().map((t) => t.id)).toEqual(['t4']);

    setQuery({ filter: 'assistant' });
    expect(component().topics().map((t) => t.id)).toEqual(['t2']);

    setQuery({ filter: 'connected-app' });
    expect(component().topics().map((t) => t.id)).toEqual(['t4']);

    setQuery({});
    expect(component().topics().map((t) => t.id)).toEqual(['t2', 't4', 't3', 't1']);
  });

  it('searches by name with the term in the URL', () => {
    setQuery({ q: 'tolk' });
    expect(component().topics().map((t) => t.id)).toEqual(['t3']);
    expect(component().searchDraft()).toBe('tolk');
  });

  it('writes the filter and search to the URL', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    component().setFilter('untyped');
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { filter: 'untyped' },
      queryParamsHandling: 'merge',
    }));
  });

  it('selects a range with shift-click from the anchor', () => {
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t1'), true, true);
    expect([...component().selected()].sort()).toEqual(['t1', 't2', 't3', 't4']);
  });

  it('sets a kind on the selection with one bulk request', () => {
    checkbox(indexOf('t1'), true);
    checkbox(indexOf('t3'), true);

    component().applyKind('k1');

    expect(setTopicsKind).toHaveBeenCalledTimes(1);
    expect(setTopicsKind.mock.calls[0][0].sort()).toEqual(['t1', 't3']);
    expect(setTopicsKind.mock.calls[0][1]).toBe('k1');
    expect(component().selectedCount()).toBe(0);
  });

  it('clears the kind on the selection with one bulk request', () => {
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t4'), true);

    component().clearKind();

    expect(setTopicsKind).toHaveBeenCalledTimes(1);
    expect(setTopicsKind.mock.calls[0][0].sort()).toEqual(['t2', 't4']);
    expect(setTopicsKind.mock.calls[0][1]).toBeNull();
  });

  it("keeps the reader's choice by re-saving each selected kind as reader", () => {
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t4'), true);

    component().keepSelection();

    expect(setTopicsKind).toHaveBeenCalledTimes(2);
    expect(setTopicsKind.mock.calls[0]).toEqual([['t2'], 'k1']);
    expect(setTopicsKind.mock.calls[1]).toEqual([['t4'], 'k2']);
  });

  it('offers the filter chips the sorter needs', () => {
    const values = component().kindChips().map((chip) => chip.value);
    expect(values).toEqual(expect.arrayContaining(['all', 'untyped', 'k1', 'k2', 'assistant', 'connected-app']));
  });

  it('renames a kind in place and rereads the kind metadata', () => {
    const reload = vi.spyOn(TestBed.inject(BrainStateService), 'reloadKinds');
    component().startRename(person);
    component().commitRename('People');
    expect(updateKind).toHaveBeenCalledWith('k1', { name: 'People' });
    expect(reload).toHaveBeenCalled();
  });

  it('creates a kind with the next free colour', () => {
    component().startNewKind();
    component().newKindName.set('Field');
    component().createKind();
    expect(createKind).toHaveBeenCalledWith({ name: 'Field', colorKey: 'kind-3' });
  });

  it('disables New kind at twelve and says why', () => {
    kinds = Array.from({ length: 12 }, (_, i) => kind(`k${i + 1}`, `Kind ${i + 1}`, `kind-${i + 1}`, i + 1));
    TestBed.inject(BrainStateService).reloadKinds();
    fixture.detectChanges();

    expect(component().atKindLimit()).toBe(true);
    const button = fixture.nativeElement.querySelector('.new-kind') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('.kind-limit')?.textContent).toContain('All 12 kinds are in use');
  });

  it('changes to a free colour without asking', () => {
    component().chooseColor(person, 'kind-5');
    expect(updateKind).toHaveBeenCalledWith('k1', { colorKey: 'kind-5', swapColor: false });
  });

  it('offers a swap when the colour is taken, and only swaps when confirmed', () => {
    component().chooseColor(person, 'kind-2');
    expect(component().pendingSwap()?.other.id).toBe('k2');
    expect(updateKind).not.toHaveBeenCalled();

    component().confirmSwap();
    expect(updateKind).toHaveBeenCalledWith('k1', { colorKey: 'kind-2', swapColor: true });
  });

  it('reorders kinds by trading sort orders', () => {
    component().moveKind(person, 1);
    expect(updateKind).toHaveBeenCalledWith('k1', { sortOrder: 2 });
    expect(updateKind).toHaveBeenCalledWith('k2', { sortOrder: 1 });
  });

  it('asks before deleting and keeps the topics', () => {
    component().askDelete(person);
    expect(component().deleteDescription()).toBe('34 topics will have no kind. The topics stay.');

    component().deleteKind();
    expect(deleteKind).toHaveBeenCalledWith('k1');
  });

  it('suggests Person for an untyped topic named after a library author', () => {
    const ursula = component().topics().find((item) => item.id === 't1')!;
    const tolkien = component().topics().find((item) => item.id === 't3')!;

    expect(component().personSuggestion(ursula)?.id).toBe('k1');
    expect(component().personSuggestion(tolkien)?.id).toBe('k1');

    const event = new MouseEvent('click');
    component().acceptPerson(tolkien, person, event);
    expect(setTopicsKind).toHaveBeenCalledWith(['t3'], 'k1');
  });

  it('does not suggest Person for a topic that already has a kind', () => {
    const education = component().topics().find((item) => item.id === 't2')!;
    expect(component().personSuggestion(education)).toBeNull();
  });
});
