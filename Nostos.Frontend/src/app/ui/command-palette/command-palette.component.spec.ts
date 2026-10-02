import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';

import { CommandPalette } from './command-palette.component';
import { BooksService } from '../../core/services/books.service';
import { CollectionsService } from '../../core/services/collections.service';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { LibraryFilterService } from '../../library/library-filter.service';

describe('CommandPalette (Cmd/Ctrl+K)', () => {
  let fixture: ComponentFixture<CommandPalette>;
  let component: CommandPalette;
  let router: Router;

  const booksList = vi.fn(() => of({ items: [], totalCount: 0 }));
  const collectionsList = vi.fn(() =>
    of([
      { id: 'c1', name: 'Classics', parentId: null },
      { id: 'c2', name: 'Sci-Fi', parentId: null },
    ]),
  );

  const openHighlightImport = vi.fn();

  beforeEach(async () => {
    localStorage.clear();
    openHighlightImport.mockClear();
    booksList.mockClear();
    collectionsList.mockClear();

    await TestBed.configureTestingModule({
      imports: [CommandPalette],
      providers: [
        provideRouter([]),
        { provide: BooksService, useValue: { list: booksList } },
        { provide: CollectionsService, useValue: { list: collectionsList } },
        { provide: HighlightImportService, useValue: { open: openHighlightImport } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CommandPalette);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true as never);
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.clear();
  });

  it('starts closed and opens on toggle, loading collections once', () => {
    expect(component.isOpen()).toBe(false);

    component.open();
    expect(component.isOpen()).toBe(true);
    expect(collectionsList).toHaveBeenCalledTimes(1);

    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="palette-input"]')).toBeTruthy();
  });

  it('toggles on Cmd+K / Ctrl+K (with or without Shift) and closes on Escape', () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', metaKey: true }));
    expect(component.isOpen()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(component.isOpen()).toBe(false);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
    expect(component.isOpen()).toBe(true);
    component.close();

    // Shift+K also toggles: plain Ctrl+K collides with the browser's own
    // search in some setups.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'K', ctrlKey: true, shiftKey: true }));
    expect(component.isOpen()).toBe(true);
    component.close();
  });

  it('lists go-to actions and filters them by query', () => {
    component.open();
    expect(component.entries().filter((e) => e.kind === 'action').length).toBe(5);

    component.onQueryChange('studio');
    const actions = component.entries().filter((e) => e.kind === 'action');
    expect(actions.map((a) => a.label)).toEqual(['Go to Writing Studio']);
  });

  it('opens the highlight import dialog in place, without navigating', () => {
    component.open();
    component.onQueryChange('kobo');
    const [action] = component.entries().filter((e) => e.kind === 'action');

    component.run(action);

    expect(openHighlightImport).toHaveBeenCalledTimes(1);
    expect(router.navigate).not.toHaveBeenCalled();
    expect(component.isOpen()).toBe(false);
  });

  it('filters collections client-side and navigates with the filter set', () => {
    component.open();
    // open() loads the sidebar list through the mocked service (synchronous of()).
    component.onQueryChange('sci');

    const cols = component.entries().filter((e) => e.kind === 'collection');
    expect(cols.map((c) => c.label)).toEqual(['Sci-Fi']);

    component.run(cols[0]);
    expect(component.isOpen()).toBe(false);
    expect(router.navigate).toHaveBeenCalledWith(['/library']);
    // The library filter carries the collection, so the landing page shows it.
    expect(TestBed.inject(LibraryFilterService).collectionId()).toBe('c2');
  });

  it('runs a book entry into its detail page and moves with arrows + Enter', () => {
    component.open();
    component.books.set([
      { id: 'b1', title: 'Dune', author: 'Frank Herbert' } as never,
      { id: 'b2', title: 'Dune Messiah', author: 'Frank Herbert' } as never,
    ]);
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('[data-testid="palette-item-book"]');
    expect(items.length).toBe(2);

    component.moveActive(1);
    expect(component.activeIndex()).toBe(1);
    component.moveActive(1);
    // Wraps within the full entry list (books + collections + actions).
    expect(component.activeIndex()).toBe(2);

    component.activeIndex.set(0);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(router.navigate).toHaveBeenCalledWith(['/library', 'b1']);
    expect(component.isOpen()).toBe(false);
  });
});
