import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryToolbar } from './library-toolbar.component';
import type { LibraryFilterChip } from './library-toolbar.component';

@Component({
  standalone: true,
  imports: [LibraryToolbar],
  template: `
    <app-library-toolbar
      [searchQuery]="search()"
      [activeSort]="sort()"
      [sortOptions]="sortOptions"
      [viewMode]="view()"
      [viewToggleOptions]="viewOptions"
      [selecting]="selecting()"
      [sidebarExpanded]="sidebar()"
      [scopeLabel]="scope()"
      [chips]="chips()"
      (search)="searches.push($event)"
      (sortChanged)="sorts.push($event)"
      (viewModeChanged)="views.push($event)"
      (sidebarToggled)="sidebarToggles.push(true)"
      (selectingToggled)="selectToggles.push(true)"
      (addBook)="adds.push(true)"
      (chipCleared)="cleared.push($event)"
    />
  `,
})
class ToolbarHost {
  readonly search = signal('');
  readonly sort = signal('lastRead');
  readonly view = signal('grid');
  readonly selecting = signal(false);
  readonly sidebar = signal(true);
  readonly scope = signal('All books');
  readonly chips = signal<LibraryFilterChip[]>([]);
  readonly sortOptions = [
    { value: 'lastRead', label: 'Last read' },
    { value: 'title', label: 'Title (A-Z)' },
  ];
  readonly viewOptions = [
    { value: 'list', icon: 'list-bullets', label: 'List view' },
    { value: 'grid', icon: 'squares-four', label: 'Grid view' },
  ] as const;
  readonly searches: Event[] = [];
  readonly sorts: string[] = [];
  readonly views: string[] = [];
  readonly sidebarToggles: boolean[] = [];
  readonly selectToggles: boolean[] = [];
  readonly adds: boolean[] = [];
  readonly cleared: string[] = [];
}

describe('LibraryToolbar', () => {
  let fixture: ComponentFixture<ToolbarHost>;
  let host: ToolbarHost;
  const el = <T extends HTMLElement>(selector: string) => fixture.nativeElement.querySelector(selector) as T | null;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({ imports: [ToolbarHost] }).compileComponents();
    fixture = TestBed.createComponent(ToolbarHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('collapses the chip strip when nothing is filtered, and draws one chip per filter', () => {
    // The bar keeps zero height with no chips, so the first chip never slides
    // content underneath the sticky toolbar.
    expect(el('.filter-bar')!.classList.contains('is-empty')).toBe(true);
    expect(fixture.nativeElement.querySelectorAll('.filter-chip').length).toBe(0);

    host.chips.set([
      { key: 'status', label: 'Favourites' },
      { key: 'search', label: '"stoic"' },
    ]);
    fixture.detectChanges();

    expect(el('.filter-bar')!.classList.contains('is-empty')).toBe(false);
    const chips = Array.from(fixture.nativeElement.querySelectorAll('.filter-chip')) as HTMLElement[];
    expect(chips.map((chip) => chip.textContent?.trim())).toEqual(['Favourites', '"stoic"']);
    // Only the shelf's own scope chips carry the scope modifier; the search is not a shelf.
    expect(chips[0].classList.contains('filter-chip--scope')).toBe(true);
    expect(chips[1].classList.contains('filter-chip--scope')).toBe(false);
    expect(chips[0].getAttribute('title')).toBe('Clear Favourites');
  });

  it('reports the chip that was cleared instead of clearing anything itself', () => {
    host.chips.set([{ key: 'collection', label: 'Classics' }]);
    fixture.detectChanges();

    (el('.filter-chip') as HTMLButtonElement).click();

    expect(host.cleared).toEqual(['collection']);
  });

  it('reports search, sort, view, select and Add book as intents', () => {
    const search = el<HTMLInputElement>('.search-input')!;
    search.value = 'dune';
    search.dispatchEvent(new Event('input'));
    expect(host.searches.length).toBe(1);

    (el('.select-toggle') as HTMLButtonElement).click();
    expect(host.selectToggles).toEqual([true]);
    expect(el('.select-toggle')!.getAttribute('aria-pressed')).toBe('false');

    (el('.library-add-button') as HTMLButtonElement).click();
    expect(host.adds).toEqual([true]);

    (el('.floating-toggle') as HTMLButtonElement).click();
    expect(host.sidebarToggles).toEqual([true]);
  });

  it('names the drawer opener for assistive tech and reflects whether the drawer is open', () => {
    const opener = el('.floating-toggle')!;
    expect(opener.getAttribute('aria-label')).toBe('Close collections sidebar');
    expect(opener.getAttribute('aria-expanded')).toBe('true');
    expect(opener.getAttribute('aria-controls')).toBe('library-collections-sidebar');

    host.sidebar.set(false);
    fixture.detectChanges();

    expect(el('.floating-toggle')!.getAttribute('aria-label')).toBe('Open collections sidebar');
    expect(el('.floating-toggle')!.getAttribute('aria-expanded')).toBe('false');

    // The shelf's name rides the opener on desktop and the chip row on a phone.
    expect(el('.scope-label')!.textContent?.trim()).toBe('All books');
  });

  it('keeps the view toggle accessible and its two options named', () => {
    const group = el('.control-group')!;
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe('Book view');

    const options = Array.from(fixture.nativeElement.querySelectorAll('.vt-opt')) as HTMLButtonElement[];
    expect(options.map((option) => option.getAttribute('aria-label'))).toEqual([
      'List view',
      'Grid view',
    ]);
    expect(options[1].getAttribute('aria-pressed')).toBe('true');
  });

  it('offers the same sort twice: the labelled field and the phone icon control', () => {
    const field = el('.sort-select')!;
    const phone = el('.sort-select-phone')!;
    expect(field.tagName).toBe('APP-DROPDOWN');
    expect(phone.tagName).toBe('APP-DROPDOWN');
    expect(phone.classList.contains('nostos-dropdown--icon')).toBe(true);
    expect(phone.querySelector('.nostos-dropdown__trigger')?.getAttribute('aria-label')).toBe(
      'Sort books',
    );
  });
});
