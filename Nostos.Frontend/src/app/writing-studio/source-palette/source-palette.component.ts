import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, debounceTime, map, of, switchMap } from 'rxjs';

import { WritingSourceDto } from '../../core/dtos/writing.dtos';
import { BooksService } from '../../core/services/books.service';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
import { NotesService } from '../../core/services/notes.service';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import {
  PaletteChoice,
  PaletteInsertMode,
  PaletteSource,
  correctQuery,
  fromKeptSource,
  fromSearchHit,
  highlightSegments,
  queryWords,
  rankSources,
  readRecentSources,
  rememberRecentSource,
  sourceByline,
  vocabularyOf,
} from './source-palette.helpers';

export interface PaletteAnchor {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

const VISIBLE_ROWS = 6;
const SEARCH_LIMIT = 20;
const DESKTOP_WIDTH = 440;
const MAX_HEIGHT = 420;
/** The floating dock and its margin, which the palette never covers. */
const DOCK_CLEARANCE = 104;

/**
 * The `@` source palette (issue #1039): a small list at the caret that finds
 * quotes, notes and passages by what they say, who wrote them and which book
 * they are from. It is not a modal; the caret stays in the page, and the
 * editor forwards the keys the palette needs.
 */
@Component({
  selector: 'app-source-palette',
  standalone: true,
  imports: [IconButtonComponent],
  templateUrl: './source-palette.component.html',
  styleUrl: './source-palette.component.css',
  host: {
    '[class.is-open]': 'open()',
  },
})
export class SourcePaletteComponent {
  private readonly notes = inject(NotesService);
  private readonly books = inject(BooksService);
  private readonly host = inject(ElementRef<HTMLElement>);

  /** The text typed after `@`, or null while no query is open. */
  readonly query = input<string | null>(null);
  /** Where the `@` is on screen; the palette sits below it, or above near the bottom. */
  readonly anchor = input<PaletteAnchor | null>(null);
  /** Sources already kept with the writing, offered first for an empty query. */
  readonly keptSources = input<readonly WritingSourceDto[]>([]);

  readonly choose = output<PaletteChoice>();
  readonly browse = output<string>();

  readonly open = computed(() => this.query() !== null);
  readonly items = signal<PaletteSource[]>([]);
  readonly active = signal(0);
  readonly loading = signal(false);
  /** The query the shown results answer, after any spelling correction. */
  readonly answered = signal('');
  /** Which row's other insert options are showing (coarse pointers). */
  readonly moreFor = signal<string | null>(null);

  readonly words = computed(() => queryWords(this.answered()));
  readonly activeId = computed(() => {
    const index = this.active();
    return this.open() && this.items()[index] ? `source-option-${index}` : null;
  });
  readonly heading = computed(() => {
    const q = (this.query() ?? '').trim();
    if (!q) return this.items().length ? 'Kept with this writing, and recent' : '';
    const corrected = this.answered();
    return corrected && corrected !== q.toLowerCase() && corrected !== q ? `Showing results for “${corrected}”` : '';
  });
  readonly position = computed(() => this.place(this.anchor()));

  private vocabulary: Set<string> | null = null;
  private vocabularyLoading = false;
  private readonly search$ = new Subject<string>();
  /** Enter pressed while results were still coming: the mode to pick with. */
  private pendingPick: PaletteInsertMode | null = null;
  /** Recents belong to the signed-in account; self-hosted is one 'local' reader. */
  private recentScope = 'local';

  constructor() {
    const destroyRef = inject(DestroyRef);

    inject(CloudAuthService)
      .sessionChanges$.pipe(takeUntilDestroyed(destroyRef))
      .subscribe((session) => (this.recentScope = session.accountId ?? 'signed-out'));

    this.search$
      .pipe(
        debounceTime(120),
        switchMap((raw) =>
          raw === ''
            ? of({ raw, query: raw, hits: [] })
            : this.notes.search(raw, SEARCH_LIMIT).pipe(
                switchMap((hits) => {
                  if (hits.length > 0 || !this.vocabulary) return of({ raw, query: raw, hits });
                  // Nothing matched as typed: try the nearest author or title
                  // spelling ("schopenhaur" -> "schopenhauer") before giving up.
                  const corrected = correctQuery(raw, this.vocabulary);
                  return corrected === raw
                    ? of({ raw, query: raw, hits })
                    : this.notes
                        .search(corrected, SEARCH_LIMIT)
                        .pipe(map((h) => ({ raw, query: corrected, hits: h })));
                }),
                catchError(() => of({ raw, query: raw, hits: [] })),
              ),
        ),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe(({ raw, query, hits }) => {
        // An answer to a query no longer on the page (typed on past, or
        // deleted back to a bare @) is dropped, never shown.
        if (raw === '' || (this.query() ?? '').trim() !== raw) return;
        const kept = new Set(this.keptSources().map((s) => s.id));
        this.answered.set(query);
        this.setItems(rankSources(hits.map((h) => fromSearchHit(h, kept)), queryWords(query)));
        this.loading.set(false);
        const pending = this.pendingPick;
        this.pendingPick = null;
        const first = this.items()[0];
        if (pending && first) this.pick(first, pending);
      });

    effect(() => {
      const query = this.query();
      if (query === null) {
        this.items.set([]);
        this.moreFor.set(null);
        this.loading.set(false);
        this.pendingPick = null;
        this.search$.next('');
        return;
      }
      untracked(() => {
        this.ensureVocabulary();
        const trimmed = query.trim();
        if (!trimmed) {
          // Cancels any search still in flight for a longer query.
          this.search$.next('');
          this.answered.set('');
          this.loading.set(false);
          this.setItems(this.emptyQueryItems());
          return;
        }
        this.loading.set(true);
        this.search$.next(trimmed);
      });
    });
  }

  /**
   * Keys the editor forwards while a query is open. True consumes the key.
   * Enter quotes, Shift+Enter inserts as a note, Ctrl/Cmd+Enter as a reference.
   */
  handleKey(event: KeyboardEvent): boolean {
    if (!this.open()) return false;
    const count = this.items().length;
    switch (event.key) {
      case 'ArrowDown':
        if (!count) return false;
        this.setActive((this.active() + 1) % count);
        return true;
      case 'ArrowUp':
        if (!count) return false;
        this.setActive((this.active() - 1 + count) % count);
        return true;
      case 'Enter': {
        const mode: PaletteInsertMode = event.shiftKey
          ? 'note'
          : event.metaKey || event.ctrlKey
            ? 'reference'
            : 'quote';
        // The list on screen may answer an earlier query: wait for this one.
        if (this.loading()) {
          this.pendingPick = mode;
          return true;
        }
        const item = this.items()[this.active()];
        if (!item) return false;
        this.pick(item, mode);
        return true;
      }
      default:
        return false;
    }
  }

  pick(item: PaletteSource, mode: PaletteInsertMode = 'quote'): void {
    // A note with no quotation is a thought, not a quote: it goes in as a note.
    const effective = mode === 'quote' && item.kind === 'note' && !item.note && !item.selectedText?.trim() ? 'note' : mode;
    this.moreFor.set(null);
    this.choose.emit({ source: item, mode: effective });
  }

  /** Records a source as recently used, for this account. */
  remember(source: PaletteSource): void {
    rememberRecentSource(source, this.recentScope);
  }

  browseReference(): void {
    this.browse.emit((this.query() ?? '').trim());
  }

  toggleMore(item: PaletteSource, event: Event): void {
    event.stopPropagation();
    this.moreFor.update((id) => (id === item.id ? null : item.id));
  }

  setActive(index: number): void {
    this.active.set(index);
    queueMicrotask(() =>
      this.host.nativeElement
        .querySelector(`#source-option-${index}`)
        ?.scrollIntoView?.({ block: 'nearest' }),
    );
  }

  byline(item: PaletteSource): string {
    return sourceByline(item);
  }

  segments(text: string) {
    return highlightSegments(text, this.words());
  }

  private setItems(items: PaletteSource[]): void {
    this.items.set(items);
    this.active.set(0);
  }

  private emptyQueryItems(): PaletteSource[] {
    const kept = this.keptSources().map(fromKeptSource);
    const keptIds = new Set(kept.map((s) => s.id));
    const recent = readRecentSources(this.recentScope).filter((s) => !keptIds.has(s.id));
    return [...kept, ...recent].slice(0, VISIBLE_ROWS * 2);
  }

  /** Author and title words, loaded once, for spelling correction. */
  private ensureVocabulary(): void {
    if (this.vocabulary || this.vocabularyLoading) return;
    this.vocabularyLoading = true;
    // The most recent 500 books: enough to correct the authors a writer is
    // working with without paging the whole library on every Studio visit.
    this.books.list({ page: 1, pageSize: 500 }).subscribe({
      next: (page) => {
        this.vocabulary = vocabularyOf(page.items.flatMap((b) => [b.title, b.author]));
        this.vocabularyLoading = false;
      },
      // A failed load is tried again on the next query.
      error: () => (this.vocabularyLoading = false),
    });
  }

  /**
   * Below the `@` when it fits, above it near the bottom of the window. On a
   * narrow screen it is a sheet just above the soft keyboard.
   */
  private place(anchor: PaletteAnchor | null): { mode: 'popover' | 'sheet'; style: Record<string, string> } {
    const vw = typeof window === 'undefined' ? 1280 : window.innerWidth;
    const vh = typeof window === 'undefined' ? 800 : window.innerHeight;
    if (vw <= 640) {
      const vv = typeof window === 'undefined' ? null : window.visualViewport;
      const keyboard = vv ? Math.max(0, vh - vv.height - vv.offsetTop) : 0;
      return { mode: 'sheet', style: { bottom: `${keyboard + 8}px` } };
    }
    if (!anchor) return { mode: 'popover', style: { top: '25vh', left: `${Math.max(16, (vw - DESKTOP_WIDTH) / 2)}px` } };
    const left = Math.min(Math.max(12, anchor.left - 8), vw - DESKTOP_WIDTH - 12);
    // Room below the `@` stops short of the floating dock; above, of the header.
    const below = vh - anchor.bottom - DOCK_CLEARANCE;
    const above = anchor.top - 24;
    if (below >= Math.min(MAX_HEIGHT, 260) || below >= above) {
      return {
        mode: 'popover',
        style: { top: `${anchor.bottom + 6}px`, left: `${left}px`, 'max-height': `${Math.max(160, Math.min(MAX_HEIGHT, below))}px` },
      };
    }
    return {
      mode: 'popover',
      style: { bottom: `${vh - anchor.top + 6}px`, left: `${left}px`, 'max-height': `${Math.min(MAX_HEIGHT, above)}px` },
    };
  }
}
