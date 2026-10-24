import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';

import { SidebarCollections } from './sidebar-collections.component';
import { CollectionsService } from '../../core/services/collections.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { ToastService } from '../../core/services/toast.service';
import { Collection } from '../../core/dtos/collection.dtos';
import { ReadingListService } from '../../core/services/reading-list.service';
import { LibraryChangesService } from '../../core/services/library-changes.service';

describe('SidebarCollections', () => {
  let component: SidebarCollections;
  let fixture: ComponentFixture<SidebarCollections>;
  let collectionsService: {
    sidebarExpanded: ReturnType<typeof signal<boolean>>;
    list: ReturnType<typeof vi.fn>;
    getCounts: ReturnType<typeof vi.fn>;
    create: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    delete: ReturnType<typeof vi.fn>;
  };
  let toast: { error: ReturnType<typeof vi.fn>; info: ReturnType<typeof vi.fn>; success: ReturnType<typeof vi.fn> };
  let readingList: {
    loaded: ReturnType<typeof signal<boolean>>;
    count: ReturnType<typeof signal<number>>;
    load: ReturnType<typeof vi.fn>;
  };

  const sampleCollections: Collection[] = [{ id: 'c1', name: 'Philosophy', parentId: null }];
  const rootCollection: Collection = { id: 'root-1', name: 'Root', parentId: null };
  const nestedCollection: Collection = { id: 'nested-1', name: 'Nested', parentId: 'root-1' };

  /** "The library changed elsewhere", driven per test. */
  let libraryChanged: Subject<void>;

  beforeEach(async () => {
    libraryChanged = new Subject<void>();
    localStorage.clear();
    collectionsService = {
      sidebarExpanded: signal(true),
      list: vi.fn(() => of(sampleCollections)),
      getCounts: vi.fn(() => of([{ collectionId: 'c1', bookCount: 5 }])),
      create: vi.fn(() => of({ id: 'c2', name: 'New', parentId: null })),
      update: vi.fn(() => of({ id: 'c1', name: 'Philosophy', parentId: null })),
      delete: vi.fn(() => of(null)),
    };
    toast = { error: vi.fn(), info: vi.fn(), success: vi.fn() };
    readingList = {
      loaded: signal(false),
      count: signal(0),
      load: vi.fn(() => {
        readingList.count.set(7);
        readingList.loaded.set(true);
        return of([]);
      }),
    };

    await TestBed.configureTestingModule({
      imports: [SidebarCollections],
      providers: [
        provideRouter([]),
        { provide: ReadingListService, useValue: readingList },
        { provide: CollectionsService, useValue: collectionsService },
        { provide: ToastService, useValue: toast },
        { provide: LibraryChangesService, useValue: { watch: () => libraryChanged } },
      ],
    }).compileComponents();

    const prefs = TestBed.inject(LibraryPreferencesService);
    prefs.setSidebarExpanded(true);
    fixture = TestBed.createComponent(SidebarCollections);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('re-reads collections and counts when the library changes elsewhere', () => {
    fixture.detectChanges();
    collectionsService.list.mockClear();
    collectionsService.getCounts.mockClear();

    libraryChanged.next();
    TestBed.flushEffects();

    expect(collectionsService.list).toHaveBeenCalledTimes(1);
    expect(collectionsService.getCounts).toHaveBeenCalledTimes(1);
  });

  it('holds the re-read while a collection is being renamed inline', () => {
    fixture.detectChanges();
    collectionsService.list.mockClear();
    component.editingId.set('c1');

    libraryChanged.next();
    TestBed.flushEffects();
    expect(collectionsService.list).not.toHaveBeenCalled();

    component.editingId.set(null);
    TestBed.flushEffects();
    expect(collectionsService.list).toHaveBeenCalledTimes(1);
  });

  it('does not own selection state on the HTTP service', () => {
    const service = collectionsService as unknown as Record<string, unknown>;
    expect(service['activeCollectionId']).toBeUndefined();
  });

  it('links to the reading list with its count, outside the filters', () => {
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('a.nav-item[title="To read"]') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/library/to-read');
    expect(link.querySelector('.count-badge')?.textContent?.trim()).toBe('7');
    expect(readingList.load).toHaveBeenCalledTimes(1);
    // Opening the list is navigation, so no library filter is touched.
    expect(component.filters.status()).toBe('all');
  });

  it('hides the reading list count when the list is empty', () => {
    fixture.detectChanges();
    readingList.count.set(0);
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('a.nav-item[title="To read"]') as HTMLAnchorElement;
    expect(link.querySelector('.count-badge')).toBeNull();
  });

  it('renders count badges from the counts fetch', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Philosophy');
    expect(fixture.nativeElement.textContent).toContain('5');
  });

  it('selection sets the collection filter (no navigation)', () => {
    component.select('c1');
    expect(component.filters.collectionId()).toBe('c1');
  });

  it('selecting the active collection toggles it off', () => {
    component.select('c1');
    component.select('c1');
    expect(component.filters.collectionId()).toBeNull();
  });

  it('rename sends both name and parentId', () => {
    component.saveRename('c1', 'Renamed');
    expect(collectionsService.update).toHaveBeenCalledWith('c1', {
      name: 'Renamed',
      parentId: null,
    });
  });

  it('move sends both name and parentId', () => {
    component.moveCollection({ id: 'c1', name: 'Philosophy', parentId: null }, 'c2');
    expect(collectionsService.update).toHaveBeenCalledWith('c1', {
      name: 'Philosophy',
      parentId: 'c2',
    });
  });

  /**
   * Delete is now a two-step flow through the in-app `ConfirmModal` rather than
   * `window.confirm()`. These helpers drive it the way the template does:
   * the row action OPENS the dialog, and only `confirmDelete()` performs the
   * delete.
   */
  const openDelete = (id: string) => {
    component.deleteCollection(id);
    fixture.detectChanges();
  };
  const acceptDelete = () => {
    component.confirmDelete();
    fixture.detectChanges();
  };

  it('delete of the active collection clears the collection filter', async () => {
    component.select('c1');
    fixture.detectChanges();

    openDelete('c1');
    acceptDelete();
    await fixture.whenStable();

    expect(component.filters.collectionId()).toBeNull();
  });

  it('delete of a non-active collection keeps the active filter', async () => {
    // The delete only proceeds when the id resolves to a real collection, so
    // the fixture must actually contain c2 — otherwise this passes vacuously.
    collectionsService.list.mockReturnValue(of([...sampleCollections, { id: 'c2', name: 'Other', parentId: null }]));
    component.load();
    component.select('c1');
    fixture.detectChanges();

    openDelete('c2');
    acceptDelete();
    await fixture.whenStable();

    expect(collectionsService.delete, 'the delete must have run').toHaveBeenCalledWith('c2');
    expect(component.filters.collectionId()).toBe('c1');
  });

  it('opening delete asks in-app and does not delete until confirmed', () => {
    fixture.detectChanges();

    openDelete('c1');

    expect(component.deleteTarget()?.id, 'the dialog must target the row').toBe('c1');
    expect(collectionsService.delete, 'no delete before confirmation').not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();
  });

  it('cancelling the dialog deletes nothing', () => {
    fixture.detectChanges();

    openDelete('c1');
    component.cancelDelete();
    fixture.detectChanges();

    expect(collectionsService.delete).not.toHaveBeenCalled();
    expect(component.deleteTarget()).toBeNull();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  describe('rename', () => {
    it('sends the current parentId for a nested collection (rename must not move to root)', () => {
      collectionsService.list.mockReturnValue(of([rootCollection, nestedCollection]));
      collectionsService.update.mockReturnValue(of({}));
      component.load();

      component.saveRename('nested-1', 'Renamed');

      expect(collectionsService.update).toHaveBeenCalledWith('nested-1', {
        name: 'Renamed',
        parentId: 'root-1',
      });
    });

    it('sends an explicit null parentId for a root collection', () => {
      collectionsService.list.mockReturnValue(of([rootCollection]));
      collectionsService.update.mockReturnValue(of({}));
      component.load();

      component.saveRename('root-1', 'Renamed');

      expect(collectionsService.update).toHaveBeenCalledWith('root-1', {
        name: 'Renamed',
        parentId: null,
      });
    });

    it('keeps the field open with the conflict said under it when a sibling name collides', () => {
      collectionsService.update.mockReturnValue(
        throwError(() => ({
          error: { title: 'collection_name_conflict', detail: 'A collection named X already exists here.' },
        })),
      );
      component.startRename({ id: 'nested-1' });

      component.saveRename('nested-1', 'X');

      expect(component.renameError()).toBe('A collection named X already exists here.');
      expect(component.renameBusy()).toBe(false);
      expect(component.editingId()).toBe('nested-1');
      expect(toast.error).not.toHaveBeenCalledWith('A collection named X already exists here.');
    });

    it('closes the field once the rename is saved', () => {
      collectionsService.update.mockReturnValue(of({}));
      component.startRename({ id: 'root-1' });

      component.saveRename('root-1', 'Renamed');

      expect(component.editingId()).toBeNull();
      expect(component.renameError()).toBeNull();
    });

    it('leaves an open rename to its own field when the page is clicked', () => {
      component.startRename({ id: 'root-1' });
      component.onDocumentClick(new MouseEvent('click')); // the click that opened it
      document.body.click();
      component.onDocumentClick({ target: document.body } as unknown as MouseEvent);

      expect(component.editingId()).toBe('root-1');
    });
  });

  describe('delete', () => {
    it('explains that children must be removed first when delete is rejected', () => {
      // The target must exist in the list for the confirm step to reach the API.
      collectionsService.list.mockReturnValue(of([rootCollection]));
      component.load();
      collectionsService.delete.mockReturnValue(
        throwError(() => ({
          error: { title: 'collection_has_children', detail: 'Collection contains children.' },
        })),
      );

      openDelete('root-1');
      acceptDelete();

      expect(toast.error).toHaveBeenCalledWith('Move or delete the child collections first');
      // The list is reloaded from the server on failure, so the tree reflects
      // reality rather than a stale optimistic removal.
      expect(component.collections()).toEqual([rootCollection]);
    });

    it('confirms in-app before deleting and reloads on success', () => {
      collectionsService.list.mockReturnValue(of([rootCollection]));
      collectionsService.delete.mockReturnValue(of(undefined));
      component.load();

      openDelete('root-1');
      // Nothing happens on open — the dialog is a question, not an action.
      expect(collectionsService.delete).not.toHaveBeenCalled();

      acceptDelete();

      expect(collectionsService.delete).toHaveBeenCalledWith('root-1');
      expect(toast.success).toHaveBeenCalledWith('Deleted “Root”');
    });

    it('names the collection in the question so the dialog is unambiguous', () => {
      fixture.detectChanges();

      openDelete('c1');

      expect(component.deleteHeading()).toContain('Philosophy');
      expect(component.deleteDescription()).toContain('stay in your library');
    });
  });

  describe('move', () => {
    it('surfaces move failures instead of failing silently', () => {
      collectionsService.update.mockReturnValue(
        throwError(() => ({
          error: { title: 'collection_cycle', detail: 'A collection cannot be moved into itself.' },
        })),
      );

      component.moveCollection(nestedCollection, 'nested-1');

      expect(toast.error).toHaveBeenCalledWith('A collection cannot be moved into itself or its children');
    });

    it('skips the request when the parent is unchanged', () => {
      component.moveCollection(rootCollection, null);

      expect(collectionsService.update).not.toHaveBeenCalled();
    });
  });

  describe('create', () => {
    it('surfaces creation failures', () => {
      collectionsService.create.mockReturnValue(
        throwError(() => ({ error: { title: 'invalid_collection_name', detail: 'Collection name is required.' } })),
      );
      component.newName.set('X');

      component.create();

      expect(toast.error).toHaveBeenCalledWith('Could not create the collection');
    });
  });

  describe('status filters (single progress-filter surface)', () => {
    function navGroups(): HTMLElement[] {
      return Array.from(
        fixture.nativeElement.querySelectorAll('.nav-group') as NodeListOf<HTMLElement>,
      );
    }

    function statusButtons(): HTMLButtonElement[] {
      // The first .nav-group is the status list (All books … Finished).
      return Array.from(
        navGroups()[0].querySelectorAll('.nav-item') as NodeListOf<HTMLButtonElement>,
      );
    }

    function formatButtons(): HTMLButtonElement[] {
      // The third .nav-group holds the MEDIA format filters; the second is the
      // link to the reading list, which is not a filter.
      return Array.from(
        navGroups()[2].querySelectorAll('.nav-item') as NodeListOf<HTMLButtonElement>,
      );
    }

    function navButton(buttons: HTMLButtonElement[], label: string): HTMLButtonElement {
      return buttons.find((el) => {
        const textSpan = el.querySelector('.label');
        return (textSpan?.textContent?.trim() ?? el.textContent?.trim()) === label;
      }) as HTMLButtonElement;
    }

    function statusButton(label: string): HTMLButtonElement {
      return navButton(statusButtons(), label);
    }

    function formatButton(label: string): HTMLButtonElement {
      return navButton(formatButtons(), label);
    }

    it('renders exactly five reading states in the expected order', () => {
      fixture.detectChanges();

      const labels = statusButtons().map((el) => el.textContent?.trim());
      expect(labels).toEqual(['All books', 'Not started', 'Reading', 'Favourites', 'Finished']);
    });

    it('closes the Collections group with Unsorted, which still filters by status', () => {
      fixture.detectChanges();

      // Unsorted means "in no collection", so it is the last row of that group.
      const collections = navGroups()[navGroups().length - 1];
      const rows = Array.from(collections.querySelectorAll(':scope > .nav-item'));
      const unsorted = rows[rows.length - 1] as HTMLButtonElement;
      expect(unsorted.querySelector('.label')?.textContent?.trim()).toBe('Unsorted');

      // Still one of the six status filters, wherever it sits.
      const marked = Array.from(
        fixture.nativeElement.querySelectorAll('[data-filter-kind="status"] .label') as NodeListOf<HTMLElement>,
      ).map((el) => el.textContent?.trim());
      expect(marked).toEqual(['All books', 'Not started', 'Reading', 'Favourites', 'Finished', 'Unsorted']);

      unsorted.click();
      fixture.detectChanges();
      expect(component.filters.status()).toBe('unsorted');
      expect(unsorted.classList.contains('active')).toBe(true);
    });

    it('shows a count only when it is above zero', () => {
      fixture.detectChanges();
      component.statusCounts.set({
        all: 3,
        notStarted: 0,
        reading: 0,
        favorites: 1,
        finished: 0,
        unsorted: 0,
      });
      fixture.detectChanges();

      const count = (label: string) =>
        statusButton(label)?.querySelector('.count-badge')?.textContent?.trim() ?? null;
      expect(count('All books')).toBe('3');
      expect(count('Favourites')).toBe('1');
      expect(count('Not started')).toBeNull();
      expect(count('Reading')).toBeNull();
    });

    it('names the in-progress filter "Reading", as Book detail does, never "In Progress"', () => {
      fixture.detectChanges();

      expect(statusButton('Reading').getAttribute('title')).toBe('Reading');
      expect(fixture.nativeElement.textContent).not.toMatch(/In Progress|Not Started|To Read/);
      const titleCaseTooltips = Array.from(
        fixture.nativeElement.querySelectorAll('[title]') as NodeListOf<HTMLElement>,
      ).filter((el) => /In Progress|Not Started|To Read/.test(el.getAttribute('title') ?? ''));
      expect(titleCaseTooltips).toHaveLength(0);
    });

    it('clicking "Reading" sets the status filter (no URL involved)', async () => {
      fixture.detectChanges();

      statusButton('Reading').click();
      await fixture.whenStable();

      expect(component.filters.status()).toBe('reading');
    });

    it('clicking the active status filter toggles it off', async () => {
      fixture.detectChanges();

      statusButton('Reading').click();
      await fixture.whenStable();
      statusButton('Reading').click();
      await fixture.whenStable();

      expect(component.filters.status()).toBe('all');
    });

    it('clicking "Not started" sets status=notstarted', async () => {
      fixture.detectChanges();

      statusButton('Not started').click();
      await fixture.whenStable();

      expect(component.filters.status()).toBe('notstarted');
    });

    it('status selection keeps the existing collection filter', async () => {
      component.select('c1');
      fixture.detectChanges();

      statusButton('Reading').click();
      await fixture.whenStable();

      expect(component.filters.collectionId()).toBe('c1');
      expect(component.filters.status()).toBe('reading');
    });

    it('collection then format keeps both (order-independent)', async () => {
      fixture.detectChanges();

      component.select('c1');
      formatButton('Audiobooks').click();
      await fixture.whenStable();

      expect(component.filters.collectionId()).toBe('c1');
      expect(component.filters.format()).toBe('audiobook');
    });

    it('format then collection keeps both (order-independent)', async () => {
      fixture.detectChanges();

      formatButton('Audiobooks').click();
      component.select('c1');
      await fixture.whenStable();

      expect(component.filters.collectionId()).toBe('c1');
      expect(component.filters.format()).toBe('audiobook');
    });

    it('clicking "All books" clears status, format and collection', async () => {
      component.select('c1');
      component.toggleStatus('reading');
      component.toggleFormat('ebook');
      fixture.detectChanges();

      statusButton('All books').click();
      await fixture.whenStable();

      expect(component.filters.status()).toBe('all');
      expect(component.filters.format()).toBe('all');
      expect(component.filters.collectionId()).toBeNull();
    });

    it('active state follows the filter service', async () => {
      component.toggleStatus('finished');
      fixture.detectChanges();
      await fixture.whenStable();

      let active = fixture.nativeElement.querySelector('.nav-item.active') as HTMLElement;
      expect(active.textContent).toContain('Finished');

      component.toggleStatus('reading');
      fixture.detectChanges();
      await fixture.whenStable();

      active = fixture.nativeElement.querySelector('.nav-item.active') as HTMLElement;
      expect(active.textContent).toContain('Reading');
      expect(active.textContent).not.toContain('Finished');
    });

    it('mobile selection closes the drawer and restores focus to its opener', async () => {
      const originalWidth = window.innerWidth;
      Object.defineProperty(window, 'innerWidth', {
        value: 390,
        writable: true,
        configurable: true,
      });

      try {
        fixture.destroy();
        fixture = TestBed.createComponent(SidebarCollections);
        component = fixture.componentInstance;
        await fixture.whenStable();

        // The opener is rendered by the parent Library toolbar; model that
        // sibling relationship without making the component own two toggles.
        const toolbar = document.createElement('header');
        toolbar.className = 'toolbar-right';
        const opener = document.createElement('button');
        opener.className = 'floating-toggle';
        toolbar.appendChild(opener);
        fixture.nativeElement.parentElement.appendChild(toolbar);

        // On mobile the drawer starts closed.
        expect(component.expanded()).toBe(false);
        component.toggle();
        fixture.detectChanges();
        expect(component.expanded()).toBe(true);

        statusButton('Reading').click();
        await fixture.whenStable();

        expect(component.expanded()).toBe(false);
        expect(document.activeElement).toBe(opener);
        toolbar.remove();
      } finally {
        Object.defineProperty(window, 'innerWidth', {
          value: originalWidth,
          writable: true,
          configurable: true,
        });
      }
    });

    it('desktop selection keeps the sidebar open and does not move focus', async () => {
      fixture.detectChanges();

      statusButton('Reading').click();
      await fixture.whenStable();

      expect(component.expanded()).toBe(true);
      expect(document.activeElement).not.toBe(
        fixture.nativeElement.querySelector('.floating-toggle'),
      );
    });
  });

  describe('when the collections cannot be loaded', () => {
    it('says so, with Try again, instead of "No collections yet"', () => {
      collectionsService.list.mockReturnValueOnce(throwError(() => new Error('offline')));
      fixture = TestBed.createComponent(SidebarCollections);
      component = fixture.componentInstance;
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('app-flat-tree')).toBeNull();
      expect(el.querySelector('nostos-empty-state [role="alert"], nostos-empty-state[role="alert"]')?.textContent)
        .toContain('Collections could not be loaded.');

      (el.querySelector('nostos-empty-state button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(el.querySelector('app-flat-tree')).not.toBeNull();
    });

    it('treats failed counts the same way, rather than showing every count as 0', () => {
      collectionsService.getCounts.mockReturnValueOnce(throwError(() => new Error('offline')));
      fixture = TestBed.createComponent(SidebarCollections);
      component = fixture.componentInstance;
      fixture.detectChanges();
      expect(component.loadFailed()).toBe(true);
    });
  });
});
