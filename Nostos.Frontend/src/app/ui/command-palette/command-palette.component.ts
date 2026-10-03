import { Component, ElementRef, HostListener, ViewChild, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, switchMap, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { BooksService } from '../../core/services/books.service';
import { CollectionsService } from '../../core/services/collections.service';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { LibraryFilterService } from '../../library/library-filter.service';
import { Book } from '../../core/dtos/book.dtos';
import { Collection } from '../../core/dtos/collection.dtos';
import { NostosIconComponent } from '../icon/nostos-icon.component';

export type PaletteEntry =
  | { kind: 'book'; id: string; label: string; sub: string }
  | { kind: 'collection'; id: string; label: string; sub: string }
  // An action either navigates (`route`) or opens the highlight import dialog.
  | { kind: 'action'; id: string; label: string; sub: string; route?: string };

const ACTIONS: PaletteEntry[] = [
  { kind: 'action', id: 'go-library', label: 'Go to Library', sub: 'Browse your books', route: '/library' },
  { kind: 'action', id: 'go-brain', label: 'Go to Second Brain', sub: 'Topics and notes', route: '/second-brain' },
  { kind: 'action', id: 'go-studio', label: 'Go to Writing Studio', sub: 'Your drafts', route: '/studio' },
  { kind: 'action', id: 'go-settings', label: 'Go to Settings', sub: 'Backup and appearance', route: '/settings' },
  { kind: 'action', id: 'import-highlights', label: 'Import e-reader highlights', sub: 'Kobo and KOReader notes' },
];

/**
 * Global command palette (Cmd/Ctrl+K).
 *
 * First slice: books (server search), collections (client filter over the
 * sidebar list) and go-to actions. Topics and writings stay out until they
 * have a search endpoint and a deep-link route — listing them without a way
 * to land on them would be a worse search, not a global one.
 */
@Component({
  selector: 'app-command-palette',
  standalone: true,
  imports: [CommonModule, FormsModule, NostosIconComponent],
  templateUrl: './command-palette.component.html',
  styleUrl: './command-palette.component.css',
})
export class CommandPalette {
  private router = inject(Router);
  private booksService = inject(BooksService);
  private collectionsService = inject(CollectionsService);
  private filters = inject(LibraryFilterService);
  private highlightImport = inject(HighlightImportService);
  isOpen = signal(false);
  query = signal('');
  books = signal<Book[]>([]);
  searching = signal(false);
  activeIndex = signal(0);

  @ViewChild('paletteInput') private inputRef?: ElementRef<HTMLInputElement>;

  private collections = signal<Collection[]>([]);
  private collectionsLoaded = false;
  private searchSubject = new Subject<string>();

  filteredActions = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return ACTIONS;
    return ACTIONS.filter(
      (a) => a.label.toLowerCase().includes(q) || a.sub.toLowerCase().includes(q),
    );
  });

  filteredCollections = computed(() => {
    const q = this.query().trim().toLowerCase();
    const all = this.collections();
    const matches = !q ? all : all.filter((c) => c.name.toLowerCase().includes(q));
    return matches.slice(0, 5);
  });

  entries = computed<PaletteEntry[]>(() => {
    const out: PaletteEntry[] = [];
    for (const b of this.books().slice(0, 7)) {
      out.push({
        kind: 'book',
        id: b.id,
        label: b.title,
        sub: b.author || 'Unknown author',
      });
    }
    for (const c of this.filteredCollections()) {
      out.push({ kind: 'collection', id: c.id, label: c.name, sub: 'Collection' });
    }
    out.push(...this.filteredActions());
    return out;
  });

  constructor() {
    this.searchSubject
      .pipe(
        debounceTime(200),
        distinctUntilChanged(),
        switchMap((q) => {
          const query = q.trim();
          if (!query) {
            this.books.set([]);
            this.searching.set(false);
            return of(null);
          }
          this.searching.set(true);
          return this.booksService.list({ search: query, page: 1, pageSize: 7 });
        }),
        takeUntilDestroyed(),
      )
      .subscribe({
        next: (res) => {
          if (res) this.books.set(res.items ?? []);
          this.searching.set(false);
          this.activeIndex.set(0);
        },
        error: () => {
          this.books.set([]);
          this.searching.set(false);
        },
      });
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    // Both plain K and Shift+K: plain Cmd/Ctrl+K collides with the browser's
    // own search in some setups, so either chord toggles the palette.
    const mod = event.metaKey || event.ctrlKey;
    if (mod && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.toggle();
      return;
    }
    if (!this.isOpen()) return;
    if (event.key === 'Escape') {
      this.close();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.moveActive(1);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.moveActive(-1);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const entry = this.entries()[this.activeIndex()];
      if (entry) this.run(entry);
    }
  }

  toggle(): void {
    if (this.isOpen()) this.close();
    else this.open();
  }

  open(): void {
    this.query.set('');
    this.books.set([]);
    this.activeIndex.set(0);
    this.isOpen.set(true);
    // The input only exists after the @if renders; focus it on the next
    // macrotask so typing starts in the palette, not the page behind it.
    setTimeout(() => this.inputRef?.nativeElement.focus(), 0);
    if (!this.collectionsLoaded) {
      this.collectionsLoaded = true;
      this.collectionsService.list().subscribe({
        next: (cols) => this.collections.set(cols ?? []),
        error: () => this.collections.set([]),
      });
    }
  }

  close(): void {
    this.isOpen.set(false);
  }

  onQueryChange(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    this.searchSubject.next(value);
  }

  moveActive(delta: number): void {
    const count = this.entries().length;
    if (count === 0) return;
    this.activeIndex.set((this.activeIndex() + delta + count) % count);
  }

  run(entry: PaletteEntry): void {
    this.close();
    if (entry.kind === 'book') {
      this.router.navigate(['/library', entry.id]);
    } else if (entry.kind === 'collection') {
      this.filters.collectionId.set(entry.id);
      this.router.navigate(['/library']);
    } else if (entry.route) {
      this.router.navigate([entry.route]);
    } else {
      this.highlightImport.open();
    }
  }
}
