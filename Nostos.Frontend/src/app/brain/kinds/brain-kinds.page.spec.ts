import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OverlayModule } from '@angular/cdk/overlay';
import { ActivatedRoute, convertToParamMap, provideRouter, Router, type ParamMap } from '@angular/router';
import { readFileSync } from 'node:fs';
import { BehaviorSubject, of, Subject, throwError } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { NotesService } from '../../core/services/notes.service';
import { ToastService } from '../../core/services/toast.service';
import { TopicsService, type TopicDto, type TopicKindDto } from '../../core/services/topics.service';
import { AssistantService } from '../../ui/assistant/assistant.service';
import { BrainKindsPage, BULK_KIND_LIMIT } from './brain-kinds.page';
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
  let topicsFail: boolean;
  let setTopicsKind: ReturnType<typeof vi.fn>;
  let updateKind: ReturnType<typeof vi.fn>;
  let createKind: ReturnType<typeof vi.fn>;
  let deleteKind: ReturnType<typeof vi.fn>;
  let listKinds: ReturnType<typeof vi.fn>;
  let listBooks: ReturnType<typeof vi.fn>;
  let kindsFail: boolean;

  const component = (): BrainKindsPage => fixture.componentInstance;

  async function mount(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [BrainKindsPage, OverlayModule],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: query.asObservable(),
            snapshot: { queryParamMap: query.value },
          },
        },
        {
          provide: TopicsService,
          useValue: {
            list: () => topicsFail ? throwError(() => new Error('topics unavailable')) : of(topics),
            listKinds,
            setTopicsKind,
            updateKind,
            createKind,
            deleteKind,
          },
        },
        {
          provide: BooksService,
          useValue: { list: listBooks },
        },
        { provide: NotesService, useValue: { summary: () => of({ total: 0 }) } },
        { provide: AssistantService, useValue: { actionExecuted: new Subject() } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn(), info: vi.fn() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainKindsPage);
    fixture.detectChanges();
  }

  /** A fresh page over the data as it now stands (a failed first load, 600 topics). */
  async function remount(): Promise<void> {
    fixture.destroy();
    TestBed.resetTestingModule();
    await mount();
  }

  beforeEach(async () => {
    query = new BehaviorSubject<ParamMap>(convertToParamMap({}));
    topicsFail = false;
    topics = [
      topic('t1', 'Ursula K. Le Guin'),
      topic('t2', 'Education', 'k1', 'assistant'),
      topic('t3', 'Tolkien'),
      topic('t4', 'Story', 'k2', 'connected-app'),
    ];
    kinds = [person, idea];
    kindsFail = false;
    listBooks = vi.fn(() => of({
      items: [{ author: 'Ursula K. Le Guin' }, { author: 'J.R.R. Tolkien' }],
      totalCount: 2,
      page: 1,
      pageSize: 100,
    }));
    setTopicsKind = vi.fn((ids: string[], kindId: string | null) =>
      of({ updatedTopicIds: ids, kindId, kindSource: kindId ? 'reader' : null }));
    updateKind = vi.fn(() => of(person));
    createKind = vi.fn(() => of(kind('k3', 'Field', 'kind-3', 3)));
    deleteKind = vi.fn(() => of(undefined));
    listKinds = vi.fn(() => (kindsFail ? throwError(() => new Error('kinds unavailable')) : of(kinds)));

    await mount();
  });

  afterEach(() => {
    fixture?.destroy();
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
    const loads = listKinds.mock.calls.length;
    component().startRename(person);
    component().commitRename('k1', 'People');

    expect(updateKind).toHaveBeenCalledWith('k1', { name: 'People' });
    expect(listKinds.mock.calls.length).toBeGreaterThan(loads);
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

  it('rereads the kind counts after a topic-kind write, so the delete confirm is right', () => {
    // The server now says Person has 21 topics, not the 34 the page loaded with.
    setTopicsKind.mockImplementation((ids: string[], kindId: string | null) => {
      kinds = kinds.map((item) => item.id === 'k1' ? { ...item, topicCount: 21 } : item);
      return of({ updatedTopicIds: ids, kindId, kindSource: kindId ? 'reader' : null });
    });
    checkbox(indexOf('t2'), true);
    component().clearKind();
    fixture.detectChanges();

    component().askDelete(component().kinds().find((item) => item.id === 'k1')!);
    expect(component().deleteDescription()).toBe('21 topics will have no kind. The topics stay.');
  });

  it('keeps the selection to rows still on screen', () => {
    setQuery({ filter: 'untyped' });
    checkbox(indexOf('t1'), true);
    expect(component().selectedCount()).toBe(1);

    // The topic got a kind elsewhere (Ask Nostos, a connected app) and the list
    // reloaded; the row is no longer visible, so it is no longer selected.
    topics = topics.map((item) => item.id === 't1' ? { ...item, kindId: 'k1' } : item);
    TestBed.inject(BrainStateService).refresh();
    fixture.detectChanges();

    expect(component().selected().has('t1')).toBe(false);
    expect(component().selectedCount()).toBe(0);
  });

  it('clears the selection when the filter changes', () => {
    checkbox(indexOf('t1'), true);
    checkbox(indexOf('t3'), true);
    expect(component().selectedCount()).toBe(2);

    setQuery({ filter: 'k2' });
    expect(component().selectedCount()).toBe(0);
  });

  it('selects a range inside the filtered rows only', () => {
    setQuery({ filter: 'untyped' });
    // The untyped rows are t1 and t3, A–Z: Tolkien, Ursula K. Le Guin.
    expect(component().topics().map((item) => item.id)).toEqual(['t3', 't1']);
    checkbox(indexOf('t3'), true);
    checkbox(indexOf('t1'), true, true);

    expect([...component().selected()].sort()).toEqual(['t1', 't3']);
  });

  it('never applies a kind to nothing without saying so', () => {
    const toast = TestBed.inject(ToastService);
    component().applyKind('k1');

    expect(setTopicsKind).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalledWith('Select at least one topic first.');
  });

  it('caps Select all at the bulk limit, and a second press undoes it', () => {
    topics = Array.from({ length: 520 }, (_, i) => topic(`t${i}`, `Topic ${String(i).padStart(3, '0')}`));
    TestBed.inject(BrainStateService).refresh();
    fixture.detectChanges();

    component().toggleAll();
    expect(component().selectedCount()).toBe(BULK_KIND_LIMIT);
    expect(TestBed.inject(ToastService).info).toHaveBeenCalledWith(
      'Selected 500 topics. Select fewer at a time.');

    // The same press again undoes the capped selection instead of repeating it.
    component().toggleAll();
    expect(component().selectedCount()).toBe(0);
  });

  it('says the 500 limit in the bar and marks its buttons when a range passes it', () => {
    topics = Array.from({ length: 520 }, (_, i) => topic(`t${i}`, `Topic ${String(i).padStart(3, '0')}`));
    TestBed.inject(BrainStateService).refresh();
    fixture.detectChanges();

    checkbox(0, true);
    checkbox(510, true, true);
    expect(component().selectedCount()).toBe(511);
    expect(component().overBulkLimit()).toBe(true);
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('#kinds-bulk-limit') as HTMLElement;
    expect(note.textContent).toContain('Select at most 500 topics at once.');
    const actions = fixture.nativeElement.querySelectorAll('.selection-action') as NodeListOf<HTMLButtonElement>;
    expect(actions.length).toBe(3);
    for (const action of Array.from(actions)) {
      expect(action.disabled).toBe(true);
      expect(action.getAttribute('aria-describedby')).toBe('kinds-bulk-limit');
    }
  });

  it('says a mixed-kind Keep was partial, and refreshes the list anyway', () => {
    const toast = TestBed.inject(ToastService);
    const state = TestBed.inject(BrainStateService);
    const refresh = vi.spyOn(state, 'refresh');
    setTopicsKind.mockImplementation((ids: string[], kindId: string | null) =>
      kindId === 'k2'
        ? throwError(() => new Error('network'))
        : of({ updatedTopicIds: ids, kindId, kindSource: 'reader' }));
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t4'), true);

    component().keepSelection();
    fixture.detectChanges();

    expect(setTopicsKind).toHaveBeenCalledTimes(2);
    expect(refresh).toHaveBeenCalled();
    expect(toast.error).toHaveBeenCalledWith(
      'Kept 1 topic as yours, but 1 topic could not be kept. Try again.');
  });

  it('rereads the kinds after a failed reorder, so equal sort orders do not stick', () => {
    const state = TestBed.inject(BrainStateService);
    const refresh = vi.spyOn(state, 'refresh');
    updateKind.mockReturnValue(throwError(() => new Error('network')));

    component().moveKind(person, 1);

    expect(refresh).toHaveBeenCalled();
    expect(TestBed.inject(ToastService).error).toHaveBeenCalledWith(
      'Could not reorder the kinds. Try again.');
  });

  it('ignores a rename answer for a kind whose field has since closed', () => {
    const answer = new Subject<TopicKindDto>();
    updateKind.mockReturnValue(answer);
    const toast = TestBed.inject(ToastService);

    component().startRename(person);
    component().commitRename('k1', 'People');
    // The reader opens another kind's field while the first save is in flight.
    component().startRename(idea);

    answer.next({ ...person, name: 'People' });
    expect(component().renamingId()).toBe('k2');

    answer.error(new Error('network'));
    expect(component().renameError()).toBeNull();
    expect(toast.error).toHaveBeenCalled();
  });

  it('counts the whole list in the header while a filter is on', () => {
    setQuery({ filter: 'untyped' });
    expect(component().headerCount()).toBe('2 of 4 topics');
    expect(fixture.nativeElement.querySelector('.page-count').textContent).toContain('2 of 4 topics');

    setQuery({});
    expect(component().headerCount()).toBe('4 topics');
  });

  it('hides the repeated No kind pill under the No kind filter', () => {
    setQuery({ filter: 'untyped' });
    expect(fixture.nativeElement.querySelector('.topic-row app-topic-kind-pill')).toBeNull();

    setQuery({});
    expect(fixture.nativeElement.querySelector('.topic-row app-topic-kind-pill')).not.toBeNull();
  });

  it('folds the filter from the URL', () => {
    setQuery({ filter: 'Untyped' });
    expect(component().filter()).toBe('untyped');
    expect(component().topics().map((item) => item.id)).toEqual(['t3', 't1']);
  });

  it('shows a failure state with Try again when the topics cannot be read', async () => {
    topicsFail = true;
    await remount();

    const failed = fixture.nativeElement.querySelector('.sorter-failed') as HTMLElement;
    expect(failed).not.toBeNull();
    expect(failed.textContent).toContain('Could not load the topics');
    expect(fixture.nativeElement.querySelector('.topic-rows')).toBeNull();
    // A failure is not an empty library: no "0 topics".
    expect(fixture.nativeElement.querySelector('.sorter-count')).toBeNull();

    const button = failed.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toContain('Try again');
    topicsFail = false;
    button.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.sorter-failed')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.topic-row').length).toBe(4);
    expect(fixture.nativeElement.querySelector('.sorter-count')?.textContent).toContain('4 topics');
  });

  it('does not carry the palette glyph on a kind swatch', () => {
    const swatch = fixture.nativeElement.querySelector('.kind-swatch') as HTMLElement;
    expect(swatch.querySelector('.kind-swatch-dot')).not.toBeNull();
    expect(swatch.querySelector('nostos-icon')).toBeNull();
  });

  it('keeps each kind card its own height beside an open palette', () => {
    const css = readFileSync('src/app/brain/kinds/brain-kinds.page.css', 'utf8');
    expect(css).toMatch(/\.kind-list\s*\{[^}]*align-items:\s*start/);
  });

  it('gives the phone checkbox and swatch a 44px target without growing them', () => {
    const css = readFileSync('src/app/brain/kinds/brain-kinds.page.css', 'utf8');
    const touch = css.match(/@media \(pointer: coarse\), \(hover: none\) \{([\s\S]*?)\n\}/);
    expect(touch).toBeTruthy();
    expect(touch![1]).toMatch(/\.topic-row\s*\{[^}]*--row-check-size:\s*var\(--control-h-touch\)/);
    expect(touch![1]).toMatch(/\.kind-swatch\s*\{[^}]*width:\s*var\(--control-h-touch\)/);
    // The Person? suggestion keeps the kind pill's height and grows a hit area
    // instead of becoming a second, twice-as-tall control on a phone.
    expect(touch![1]).toMatch(/button\.person-suggestion[^{]*\{[^}]*min-height:\s*24px/);
    expect(touch![1]).toMatch(/\.person-suggestion::after\s*\{[^}]*inset:\s*-10px 0/);
  });

  it('says a colour another kind holds offers a swap, in whole sentences', () => {
    component().toggleColor(person);
    fixture.detectChanges();

    const hint = fixture.nativeElement.querySelector('.color-hint') as HTMLElement;
    expect(hint.textContent?.trim()).toBe(
      'Colours no other kind holds are free. Picking a colour another kind holds offers a swap.');
  });

  it('moves focus to the sorter heading after a bulk action', () => {
    checkbox(indexOf('t2'), true);
    fixture.detectChanges();
    const setKind = fixture.nativeElement.querySelector('.selection-action') as HTMLButtonElement;
    setKind.focus();

    component().applyKind('k1');
    fixture.detectChanges();

    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('#kinds-sorter-title'));
  });

  it('gives Clear kind its own glyph, not the selection bar close cross', () => {
    checkbox(indexOf('t2'), true);
    fixture.detectChanges();

    const actions = fixture.nativeElement.querySelectorAll('.selection-action') as NodeListOf<HTMLElement>;
    const glyphs = Array.from(actions).map(
      (action) => (action.querySelector('nostos-icon') as HTMLElement).getAttribute('name'));
    expect(glyphs[1]).toBe('minus');
    expect(glyphs.filter((name) => name === 'x')).toHaveLength(0);
  });


  /** Waits for the frame the page defers a focus move to. */
  const settleFrame = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 40));

  it('clears the search and the filter in one navigation', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    setQuery({ q: 'tolk', filter: 'untyped' });
    navigate.mockClear();

    component().clearFilters();

    // Two navigations raced, and the second cancelled the first: `q` survived.
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { q: null, filter: null },
      queryParamsHandling: 'merge',
    }));
  });

  it('caps Select all at the first 500 rows, replacing one picked past the cap', () => {
    topics = Array.from({ length: 520 }, (_, i) => ({
      id: `t${i}`,
      name: `Topic ${String(i).padStart(3, '0')}`,
      usageCount: 3,
      kindId: null,
      kindSource: null,
    })) as TopicDto[];
    TestBed.inject(BrainStateService).refresh();
    fixture.detectChanges();

    // A row past the cap is already ticked; Select all must cap at 500, not add
    // 500 more to it and send 501 to a server that refuses more than 500.
    checkbox(510, true);
    expect(component().selectedCount()).toBe(1);

    component().toggleAll();
    fixture.detectChanges();

    expect(component().selectedCount()).toBe(BULK_KIND_LIMIT);
    expect(component().selected().has('t510')).toBe(false);
    const label = (fixture.nativeElement.querySelector('.sorter-tools button') as HTMLElement)
      .textContent?.trim();
    expect(label).toBe('Clear selection');
  });

  it('ignores a second reorder click that lands before the refreshed list', () => {
    const answer = new Subject<TopicKindDto>();
    updateKind.mockReturnValue(answer);
    component().moveKind(person, 1);
    // The buttons are disabled while `busy`, but a second click can already be
    // queued; it read the same old sort orders and duplicated them.
    component().moveKind(person, 1);

    expect(updateKind).toHaveBeenCalledTimes(2);
    expect(updateKind).toHaveBeenCalledWith('k1', { sortOrder: 2 });
    expect(updateKind).toHaveBeenCalledWith('k2', { sortOrder: 1 });
  });

  it('repairs equal sort orders instead of trading two equal values', () => {
    kinds = [
      kind('k1', 'Person', 'kind-1', 1),
      kind('k2', 'Idea', 'kind-2', 1),
      kind('k3', 'Field', 'kind-3', 1),
    ];
    TestBed.inject(BrainStateService).reloadKinds();
    fixture.detectChanges();
    updateKind.mockClear();

    component().moveKind(component().kinds()[1], 1); // Idea down, past Field

    // Undoing the duplicates needs 1..N written where it differs: Person is
    // already first, Field becomes second and Idea third.
    expect(updateKind.mock.calls).toEqual([
      ['k3', { sortOrder: 2 }],
      ['k2', { sortOrder: 3 }],
    ]);
  });

  it('keeps only the failed rows selected when a Keep is partly refused', () => {
    setTopicsKind.mockImplementation((ids: string[], kindId: string | null) =>
      kindId === 'k2'
        ? throwError(() => new Error('network'))
        : of({ updatedTopicIds: ids, kindId, kindSource: 'reader' }));
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t4'), true);

    component().keepSelection();
    fixture.detectChanges();

    expect([...component().selected()]).toEqual(['t4']);
    expect(component().selectedCount()).toBe(1);
  });

  it('keeps the whole selection when every Keep request fails', () => {
    setTopicsKind.mockReturnValue(throwError(() => new Error('network')));
    checkbox(indexOf('t2'), true);
    checkbox(indexOf('t4'), true);

    component().keepSelection();
    fixture.detectChanges();

    expect(component().selectedCount()).toBe(2);
  });

  it('does not navigate from a deleted page, and drops the swap it was waiting for', () => {
    const answer = new Subject<void>();
    deleteKind.mockReturnValue(answer);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    setQuery({ filter: 'k2' });
    // A swap is waiting on Idea's colour.
    component().chooseColor(person, 'kind-2');
    expect(component().pendingSwap()?.other.id).toBe('k2');
    component().askDelete(idea);
    navigate.mockClear();

    component().deleteKind();
    // The page is left while the delete is in flight.
    fixture.destroy();
    answer.next();

    expect(navigate).not.toHaveBeenCalled();
    expect(component().pendingSwap()).toBeNull();
  });

  it('lets a second kind be renamed while the first is saving', () => {
    const first = new Subject<TopicKindDto>();
    const second = new Subject<TopicKindDto>();
    updateKind.mockReturnValueOnce(first).mockReturnValueOnce(second);

    component().startRename(person);
    component().commitRename('k1', 'People');
    expect(component().isRenaming('k1')).toBe(true);

    component().startRename(idea);
    component().commitRename('k2', 'Ideas');

    // One page-wide flag used to refuse this second save outright.
    expect(updateKind).toHaveBeenCalledTimes(2);
    expect(component().isRenaming('k2')).toBe(true);

    first.next({ ...person, name: 'People' });
    expect(component().isRenaming('k1')).toBe(false);
    expect(component().isRenaming('k2')).toBe(true);
  });

  it('closes the Set kind panel when its selection disappears with the rows', async () => {
    setQuery({ filter: 'untyped' });
    checkbox(indexOf('t1'), true);
    component().setKindOpen.set(true);
    fixture.detectChanges();
    // Let the popover's own opening focus pass run before it is closed again
    // (a panel is never closed and opened in the same tick by a reader).
    await Promise.resolve();

    // The row got a kind elsewhere and left the "No kind" view.
    topics = topics.map((item) => (item.id === 't1' ? { ...item, kindId: 'k1' } : item));
    TestBed.inject(BrainStateService).refresh();
    fixture.detectChanges();

    expect(component().selectedCount()).toBe(0);
    expect(component().setKindOpen()).toBe(false);
  });

  it('focuses the new kind field when the form opens, and the button when it closes', async () => {
    component().startNewKind();
    fixture.detectChanges();
    await settleFrame();

    const field = fixture.nativeElement.querySelector('#new-kind-name') as HTMLInputElement;
    expect(document.activeElement).toBe(field);

    component().cancelNewKind();
    fixture.detectChanges();

    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.new-kind'));
  });

  it('moves focus to the next row after answering Person?', async () => {
    const rowFor = (name: string): HTMLElement =>
      (Array.from(fixture.nativeElement.querySelectorAll('.topic-row')) as HTMLElement[])
        .find((row) => row.querySelector('.topic-name')?.textContent?.includes(name))!;

    const suggestion = rowFor('Tolkien').querySelector('.person-suggestion') as HTMLButtonElement;
    suggestion.click();
    fixture.detectChanges();
    await settleFrame();

    // Tolkien's own suggestion is gone; the next candidate keeps the reader's
    // place instead of focus falling to the page body.
    expect(rowFor('Ursula').contains(document.activeElement)).toBe(true);
  });

  it('reads only the author pages the library has, and keeps the ones that answer', async () => {
    listBooks.mockImplementation(({ page }: { page: number }) =>
      page === 2
        ? throwError(() => new Error('network'))
        : of({
          items: [{ author: page === 1 ? 'Ursula K. Le Guin' : 'J.R.R. Tolkien' }],
          totalCount: 250,
          page,
          pageSize: 100,
        }));
    listBooks.mockClear();
    await remount();

    // 250 books is three pages of 100, not a fixed five.
    expect(listBooks.mock.calls.map((call) => call[0].page)).toEqual([1, 2, 3]);

    const suggester = component();
    // Page 2 failed; the names from pages 1 and 3 are still there.
    expect(suggester.personSuggestion(topic('x', 'Ursula K. Le Guin'))?.id).toBe('k1');
    expect(suggester.personSuggestion(topic('y', 'Tolkien'))?.id).toBe('k1');
  });

  it('says the kinds could not be loaded, offers Try again, and refuses to create one', async () => {
    kindsFail = true;
    await remount();

    const failed = fixture.nativeElement.querySelector('.kinds-failed') as HTMLElement;
    expect(failed).not.toBeNull();
    expect(failed.textContent).toContain('Could not load your kinds');
    // Creating over an unknown list would pick a colour another kind holds.
    const newKind = fixture.nativeElement.querySelector('.new-kind') as HTMLButtonElement;
    expect(newKind.disabled).toBe(true);
    component().startNewKind();
    expect(component().newKindOpen()).toBe(false);

    kindsFail = false;
    (failed.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.kinds-failed')).toBeNull();
    expect((fixture.nativeElement.querySelector('.new-kind') as HTMLButtonElement).disabled).toBe(false);
  });

  it('returns focus to the kind card when a rename closes', async () => {
    component().startRename(person);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(fixture.nativeElement.querySelector('.kind-rename')).not.toBeNull();

    component().cancelRename();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));

    const card = fixture.nativeElement.querySelector('.kind-card') as HTMLElement;
    expect(card.getAttribute('data-rename-row')).not.toBeNull();
    expect(card.tabIndex).toBe(-1);
    expect(document.activeElement).toBe(card);
  });
});
