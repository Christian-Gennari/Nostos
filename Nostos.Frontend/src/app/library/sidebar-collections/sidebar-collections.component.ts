import {
  Component,
  OnInit,
  inject,
  signal,
  model,
  computed,
  HostListener,
  ElementRef,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  LibraryFilterService,
  StatusFilter,
  FormatFilter,
} from '../library-filter.service';

import { CollectionsService } from '../../core/services/collections.service';
import { Collection, CollectionCountDto } from '../../core/dtos/collection.dtos';
import { LibraryStatusCountsDto } from '../../core/dtos/book.dtos';
import { BooksService } from '../../core/services/books.service';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { FlatTreeComponent } from '../../ui/flat-tree/flat-tree.component';
import { ToastService } from '../../core/services/toast.service';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

@Component({
  standalone: true,
  selector: 'app-sidebar-collections',
  imports: [CommonModule, FormsModule, NostosIconComponent, FlatTreeComponent, ConfirmModal],
  templateUrl: './sidebar-collections.component.html',
  styleUrls: ['./sidebar-collections.component.css'],
})
export class SidebarCollections implements OnInit {
  private collectionsService = inject(CollectionsService);
  private booksService = inject(BooksService);
  private preferences = inject(LibraryPreferencesService);
  readonly filters = inject(LibraryFilterService);
  private elementRef = inject(ElementRef);
  private toast = inject(ToastService);

  // State
  collections = signal<Collection[]>([]);
  counts = signal<CollectionCountDto[]>([]);
  statusCounts = signal<LibraryStatusCountsDto | null>(null);
  expanded = this.preferences.sidebarExpanded;
  adding = signal(false);
  editingId = signal<string | null>(null);
  newName = model<string>('');
  private ignoreClick = false;

  /**
   * The collection awaiting delete confirmation, plus whether that delete is in
   * flight. Deletion asks through `ConfirmModal` (the app's single confirmation
   * surface) rather than `window.confirm()`, which cannot carry theme tokens and
   * breaks the visual language.
   */
  deleteTarget = signal<Collection | null>(null);
  deleting = signal(false);

  /**
   * The confirmation question, composed here so the modal stays generic.
   * Falls back to a bare "this collection?" only if the record somehow left the
   * list between opening and rendering.
   */
  deleteHeading = computed(() => {
    const target = this.deleteTarget();
    return target ? `Delete “${target.name}”?` : 'Delete collection?';
  });

  /**
   * What the delete actually does, so the user is not left guessing whether
   * their books go with it. Books belong to the library, not to the collection,
   * so nothing of theirs is destroyed — only the grouping is removed.
   */
  deleteDescription = computed(() => {
    const target = this.deleteTarget();
    if (!target) return '';
    const books = this.countsMap().get(target.id) ?? 0;
    const booksClause = books > 0
      ? ` Its ${books} book${books === 1 ? '' : 's'} will be moved out of the collection, not deleted.`
      : '';
    return `Books stay in your library — only the collection is removed.${booksClause}`;
  });

  readonly hasSelection = computed(
    () =>
      this.filters.status() !== 'all' ||
      this.filters.format() !== 'all' ||
      this.filters.collectionId() !== null,
  );

  readonly countsMap = computed(
    () => new Map<string, number>(this.counts().map((c) => [c.collectionId, c.bookCount])),
  );

  readonly treeItems = computed(() =>
    this.collections().map((c) => ({ ...c, count: this.countsMap().get(c.id) ?? 0 })),
  );

  // State to control initial animation
  isLoaded = signal(false);

  ngOnInit(): void {
    this.load();
    this.loadCounts();
    this.loadStatusCounts();
    if (window.innerWidth < 768) {
      this.setExpanded(false);
    }

    // Set isLoaded to true after a minimal timeout
    setTimeout(() => {
      this.isLoaded.set(true);
    }, 0);
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent) {
    if (this.ignoreClick) {
      this.ignoreClick = false;
      return;
    }

    const target = event.target as HTMLElement;

    // Mobile: Close sidebar if clicking outside
    if (window.innerWidth < 768 && this.expanded()) {
      const clickedInside = this.elementRef.nativeElement.contains(target);
      if (!clickedInside) {
        this.setExpanded(false);
        // If the sidebar is closed via outside click, we can return early
        return;
      }
    }

    if (!this.adding() && !this.editingId()) return;

    const isInsideInputRow = target.closest('.nav-item.input-mode');

    if (!isInsideInputRow) {
      if (this.adding()) this.resetInput();
      if (this.editingId()) this.cancelRename();
    }
  }

  load(): void {
    this.collectionsService.list().subscribe({
      next: (cols) => this.collections.set(cols),
      error: () => this.toast.error('Failed to load collections'),
    });
  }

  loadCounts(): void {
    this.collectionsService.getCounts().subscribe({
      next: (counts) => this.counts.set(counts),
    });
  }

  loadStatusCounts(): void {
    this.booksService.getStatusCounts().subscribe({
      next: (counts) => this.statusCounts.set(counts),
      error: () => this.toast.error('Failed to load library status counts'),
    });
  }

  toggle(): void {
    this.setExpanded(!this.expanded());
  }

  private setExpanded(expanded: boolean): void {
    this.preferences.setSidebarExpanded(expanded);
  }

  /**
   * Filter buttons only manage the mobile drawer: after a selection the
   * drawer closes and focus returns to its toolbar opener so
   * it can be reopened immediately. Desktop selection leaves the sidebar
   * untouched.
   */
  private closeDrawerOnMobile(): void {
    if (window.innerWidth < 768 && this.expanded()) {
      this.setExpanded(false);
      // The opener lives in the sibling Library toolbar, not in this drawer.
      (this.elementRef.nativeElement as HTMLElement).parentElement
        ?.querySelector<HTMLButtonElement>('.toolbar-right .floating-toggle')
        ?.focus();
    }
  }

  toggleStatus(status: StatusFilter): void {
    this.filters.toggleStatus(status);
    this.closeDrawerOnMobile();
  }

  toggleFormat(format: FormatFilter): void {
    this.filters.toggleFormat(format);
    this.closeDrawerOnMobile();
  }

  clearFilters(): void {
    this.filters.clearAll();
    this.closeDrawerOnMobile();
  }

  select(id: string | null): void {
    this.filters.toggleCollection(id);
    this.closeDrawerOnMobile();
  }

  startAdd(): void {
    this.ignoreClick = true;
    this.adding.set(true);
    if (!this.expanded()) this.setExpanded(true);
  }

  resetInput(): void {
    this.adding.set(false);
    this.newName.set('');
    this.editingId.set(null);
  }

  create(): void {
    const name = this.newName().trim();
    if (!name) {
      this.resetInput();
      return;
    }
    this.collectionsService.create({ name }).subscribe({
      next: (newCol) => {
        this.resetInput();
        this.load();
        this.loadCounts();
        this.select(newCol.id);
      },
      error: () => this.toast.error('Could not create collection.'),
    });
  }

  startRename(item: any): void {
    this.ignoreClick = true;
    this.editingId.set(item.id);
  }

  cancelRename(): void {
    this.editingId.set(null);
  }

  saveRename(id: string, newName: string): void {
    if (!newName.trim()) {
      this.cancelRename();
      return;
    }
    const collection = this.collections().find((c) => c.id === id);
    // Full-replace PUT: always send the current parentId (explicit null for
    // root) so a rename never reads as "move to root".
    this.collectionsService
      .update(id, { name: newName, parentId: collection?.parentId ?? null })
      .subscribe({
        next: () => {
          this.editingId.set(null);
          this.load();
          this.loadCounts();
        },
        error: (err) => {
          this.editingId.set(null);
          this.toast.error(this.describeCollectionError(err));
          this.load();
        },
      });
  }

  /**
   * Entry point from the row's delete action. Opens the confirmation modal
   * instead of asking through `window.confirm()`.
   */
  deleteCollection(id: string): void {
    const target = this.collections().find((c) => c.id === id) ?? null;
    if (!target) return;
    this.deleteTarget.set(target);
  }

  cancelDelete(): void {
    if (this.deleting()) return;
    this.deleteTarget.set(null);
  }

  /**
   * Runs the delete the user just confirmed. The modal is held open (and locked)
   * for the duration so the pending state is visible; on failure it stays open
   * with the server's own explanation rather than vanishing mid-action.
   */
  confirmDelete(): void {
    const target = this.deleteTarget();
    if (!target || this.deleting()) return;

    this.deleting.set(true);
    this.collectionsService.delete(target.id).subscribe({
      next: () => {
        this.toast.info('Collection deleted');
        this.load();
        this.loadCounts();
        if (this.filters.collectionId() === target.id) {
          this.filters.collectionId.set(null);
        }
        this.deleteTarget.set(null);
        this.deleting.set(false);
      },
      error: (err) => {
        this.toast.error(this.describeCollectionError(err));
        this.load();
        this.deleteTarget.set(null);
        this.deleting.set(false);
      },
    });
  }

  private describeCollectionError(err: unknown): string {
    const body = (err as { error?: { title?: string; detail?: string } })?.error;
    switch (body?.title) {
      case 'collection_has_children':
        return 'Move or delete the child collections first.';
      case 'collection_name_conflict':
        return body.detail || 'A collection with that name already exists here.';
      case 'collection_cycle':
        return 'A collection cannot be moved into itself or its children.';
      case 'invalid_collection_parent':
        return body.detail || 'The destination collection no longer exists.';
      default:
        return body?.detail || 'Something went wrong.';
    }
  }

  getNameForId(id: string): string {
    return this.collections().find((c) => c.id === id)?.name || '';
  }

  onItemMoved(event: { item: any; newParentId: string | null }) {
    this.moveCollection(event.item, event.newParentId);
  }

  moveCollection(item: Collection, newParentId: string | null) {
    if ((item.parentId ?? null) === newParentId) return;

    this.collectionsService
      .update(item.id, {
        name: item.name,
        parentId: newParentId,
      })
      .subscribe({
        next: () => {
          this.load();
          this.loadCounts();
        },
        error: (err) => {
          this.toast.error(this.describeCollectionError(err));
          this.load();
        },
      });
  }
}
