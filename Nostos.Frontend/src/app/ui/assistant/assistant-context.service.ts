/**
 * The assistant's ambient context: what the user is looking at right now.
 *
 * WHY A PROVIDER REGISTRY
 * -----------------------
 * The three readers already know their own location and selection, and later
 * streams (voice, the LLM bridge) will add more sources. Rather than teach this
 * service about EPUB, PDF and audio — which would make every new surface a
 * change here — it holds an ordered list of small provider functions and
 * resolves the FIRST non-null value per field. A reader contributes location and
 * selection without the assistant importing the reader; the streaming bridge
 * (261-S2) contributes each surface's own answer the same way.
 *
 * PRIORITY
 * --------
 * Explicit targets beat ambient ones. A provider registered as `explicit`
 * (a reader knows the book it has open) is ordered ahead of ambient providers,
 * and the route-derived base context is the final fallback. Weak or ambiguous
 * values simply stay null — a guessed anchor is worse than none, because the
 * capture would be filed against the wrong place.
 */
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs/operators';

import { BooksService } from '../../core/services/books.service';
import { LibraryFilterService } from '../../library/library-filter.service';

export type AssistantSurface =
  | 'reader'
  | 'library'
  | 'book-detail'
  | 'second-brain'
  | 'studio'
  | 'settings'
  | 'home'
  | 'other';

export type AssistantReaderType = 'epub' | 'pdf' | 'audio';
export type AssistantBookFormat = 'physical' | 'ebook' | 'audiobook';

/** Mirrors the D4 anchor kinds on the note model (enum-as-string). */
export type AssistantAnchorKind =
  | 'epub_cfi'
  | 'pdf_page'
  | 'audio_timestamp'
  | 'physical_page'
  | 'external_audio_timestamp'
  | 'unknown';

/**
 * A resolved source anchor. `verified` is true ONLY for an anchor the app
 * acquired itself (reader CFI/page, in-app timestamp); a value the user typed is
 * never verified.
 */
export interface AssistantAnchor {
  kind: AssistantAnchorKind;
  value: string | null;
  verified: boolean;
}

export interface AssistantContext {
  surface: AssistantSurface;
  route: string;
  bookId: string | null;
  bookTitle: string | null;
  bookFormat: AssistantBookFormat | null;
  readerType: AssistantReaderType | null;
  epubCfi: string | null;
  pdfPage: number | null;
  audioTimestamp: number | null;
  audioChapter: string | null;
  selectedText: string | null;
  /** Current or most recent reading target (the book id in context). */
  readingTarget: string | null;
  /** Brain review note in view; a provider supplies it in a later stream. */
  brainReviewNoteId: string | null;
  /** Current topic; a provider supplies it in a later stream. */
  topic: string | null;
  collectionId: string | null;
  anchor: AssistantAnchor | null;
}

/** A reader/surface contributes a partial snapshot; nulls are ignored. */
export type AssistantContextProvider = () => Partial<AssistantContext> | null;

export interface AssistantContextRegistration {
  /** Order ahead of ambient providers (readers do this). */
  explicit?: boolean;
}

interface AssistantBookMeta {
  id: string;
  title: string;
  format: AssistantBookFormat;
}

@Injectable({ providedIn: 'root' })
export class AssistantContextService {
  // Optional so reader unit tests (which do not provide a Router) can still
  // construct the service these readers now inject.
  private readonly router = inject(Router, { optional: true });
  private readonly books = inject(BooksService, { optional: true });
  private readonly filters = inject(LibraryFilterService, { optional: true });

  private readonly providers: AssistantContextProvider[] = [];
  private readonly registryVersion = signal(0);
  private readonly url = signal(this.router?.url ?? '');
  private readonly bookMeta = signal<AssistantBookMeta | null>(null);

  /** The resolved snapshot. Recomputes on navigation, book meta or registration. */
  readonly context = computed<AssistantContext>(() => {
    this.registryVersion();
    const base = this.baseContext();
    const result = this.emptyContext(base.surface, base.route);

    for (const provider of this.providers) {
      try {
        this.fill(result, provider());
      } catch {
        // One broken provider must not blank the whole context.
      }
    }
    this.fill(result, base);
    result.anchor = resolveAnchor(result);
    return result;
  });

  constructor() {
    const router = this.router;
    if (router) {
      this.url.set(router.url);
      router.events
        .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
        .subscribe((event) => this.url.set(event.urlAfterRedirects));
    }

    // Book identity (title/format) is ambient, route-derived. It cannot come
    // from the readers alone: the PDF reader holds no book object and Book
    // Details is not a reader at all.
    effect(() => {
      const id = this.routeBookId();
      const books = this.books;
      if (!id || !books || typeof books.get !== 'function') {
        this.bookMeta.set(null);
        return;
      }
      let cancelled = false;
      books.get(id).subscribe({
        next: (book) => {
          if (cancelled) return;
          this.bookMeta.set({ id: book.id, title: book.title, format: book.type });
        },
        error: () => {
          if (!cancelled) this.bookMeta.set(null);
        },
      });
      return () => {
        cancelled = true;
      };
    });
  }

  /**
   * Register a provider. Returns an unregister function; components must call it
   * on destroy so a closed reader stops answering for the next surface.
   */
  register(
    provider: AssistantContextProvider,
    options: AssistantContextRegistration = {},
  ): () => void {
    if (options.explicit) this.providers.unshift(provider);
    else this.providers.push(provider);
    this.registryVersion.update((v) => v + 1);

    return () => {
      const index = this.providers.indexOf(provider);
      if (index >= 0) {
        this.providers.splice(index, 1);
        this.registryVersion.update((v) => v + 1);
      }
    };
  }

  private readonly routeBookId = computed(() => this.routeInfo(this.url()).bookId);

  private baseContext(): AssistantContext {
    const url = this.url();
    const info = this.routeInfo(url);
    const meta = this.bookMeta();
    // GUIDs are case-insensitive, and the API returns a lowercased id while a
    // deep link may carry any casing, so compare without case or the title and
    // format silently vanish for a deep-linked reader.
    const metaMatches =
      meta !== null &&
      info.bookId !== null &&
      meta.id.toLowerCase() === info.bookId.toLowerCase();

    const context = this.emptyContext(info.surface, url);
    context.bookId = info.bookId;
    context.readingTarget = info.bookId;
    context.bookTitle = metaMatches ? meta.title : null;
    context.bookFormat = metaMatches ? meta.format : null;
    context.collectionId = this.filters?.collectionId() ?? null;
    return context;
  }

  private routeInfo(url: string): { surface: AssistantSurface; bookId: string | null } {
    const path = url.split('?')[0].split('#')[0];
    const segments = path.split('/').filter(Boolean);

    let surface: AssistantSurface = 'other';
    if (path === '' || path === '/') surface = 'home';
    else if (path.startsWith('/read')) surface = 'reader';
    else if (path.startsWith('/library/')) surface = 'book-detail';
    else if (path.startsWith('/library')) surface = 'library';
    else if (path.startsWith('/second-brain')) surface = 'second-brain';
    else if (path.startsWith('/studio')) surface = 'studio';
    else if (path.startsWith('/settings')) surface = 'settings';

    const bookId =
      (surface === 'reader' || surface === 'book-detail') && segments[1] ? segments[1] : null;
    return { surface, bookId };
  }

  private emptyContext(surface: AssistantSurface, route: string): AssistantContext {
    return {
      surface,
      route,
      bookId: null,
      bookTitle: null,
      bookFormat: null,
      readerType: null,
      epubCfi: null,
      pdfPage: null,
      audioTimestamp: null,
      audioChapter: null,
      selectedText: null,
      readingTarget: null,
      brainReviewNoteId: null,
      topic: null,
      collectionId: null,
      anchor: null,
    };
  }

  /** Fill only the fields still null, so the first non-null value wins. */
  private fill(target: AssistantContext, partial: Partial<AssistantContext> | null): void {
    if (!partial) return;
    const entries = Object.entries(partial) as [keyof AssistantContext, unknown][];
    for (const [key, value] of entries) {
      if (key === 'anchor') continue;
      if (value === undefined || value === null) continue;
      if (target[key] === null) {
        (target as unknown as Record<string, unknown>)[key] = value;
      }
    }
  }
}

/**
 * The single place that turns the ambient fields into a typed anchor. App-known
 * locations are verified; anything absent stays null so the composer can ask.
 */
export function resolveAnchor(context: AssistantContext): AssistantAnchor | null {
  if (context.epubCfi) {
    return { kind: 'epub_cfi', value: context.epubCfi, verified: true };
  }
  if (context.pdfPage !== null) {
    return { kind: 'pdf_page', value: String(context.pdfPage), verified: true };
  }
  if (context.audioTimestamp !== null) {
    return { kind: 'audio_timestamp', value: String(context.audioTimestamp), verified: true };
  }
  return null;
}
