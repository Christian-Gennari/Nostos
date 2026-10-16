import {
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  NgZone,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';

import { NotesService } from '../../core/services/notes.service';
import { Passage, passageReadingMinutes } from '../../core/dtos/note.dtos';
import { libronFontFaceCss } from '../../reader/epub-reader/epub-typography';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { ButtonComponent } from '../button/button.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { PassageViewerService } from './passage-viewer.service';
import { passageBodyHtml, passageSourceParams } from './passage-viewer.helpers';

type ViewerState = 'loading' | 'ready' | 'error';

/** Gap between pages, in px. Part of the step one page turn moves. */
const PAGE_GAP = 48;
/** A horizontal swipe at least this long, in px, turns the page. */
const SWIPE_DISTANCE = 48;

let libronInjected = false;

/**
 * The passage viewer (issue #1014): a saved passage read on its own, page by
 * page, wherever it was opened from. It is not a second reader. It typesets
 * the passage's saved text in the reader's own face (EPUB), and "Open in book"
 * hands over to the real reader at the passage's start.
 *
 * Pages are CSS columns one page wide in a box one page high; a page turn
 * slides the column strip by one page. The page count is measured, so it
 * follows the window, the font and the text.
 */
@Component({
  selector: 'app-passage-viewer',
  standalone: true,
  imports: [ModalShell, ButtonComponent, IconButtonComponent, NostosIconComponent],
  templateUrl: './passage-viewer.component.html',
  styleUrl: './passage-viewer.component.css',
})
export class PassageViewerComponent {
  private readonly viewer = inject(PassageViewerService);
  private readonly notes = inject(NotesService);
  private readonly router = inject(Router);
  private readonly zone = inject(NgZone);
  private readonly destroyRef = inject(DestroyRef);

  readonly request = this.viewer.request;
  readonly state = signal<ViewerState>('loading');
  readonly passage = signal<Passage | null>(null);
  readonly page = signal(0);
  readonly pageCount = signal(1);
  /** Text selected inside the passage, offered to `quoteSelection`. */
  readonly selection = signal('');
  /** Width of one page in px, measured from the frame. */
  readonly pageWidth = signal(0);

  readonly body = computed(() => {
    const passage = this.passage();
    return passage ? passageBodyHtml(passage) : '';
  });

  readonly subtitle = computed(() => {
    const passage = this.passage();
    if (!passage) return '';
    const parts = [passage.bookTitle?.trim() || 'Untitled book'];
    if (passage.wordCount > 0) {
      parts.push(
        `${passage.wordCount.toLocaleString('en-US')} words`,
        `${passageReadingMinutes(passage.wordCount)} min`,
      );
    }
    return parts.join(' · ');
  });

  readonly progress = computed(() => {
    const count = this.pageCount();
    return count <= 1 ? 100 : ((this.page() + 1) / count) * 100;
  });

  readonly offset = computed(() => -this.page() * (this.pageWidth() + PAGE_GAP));
  readonly pageGap = PAGE_GAP;

  private readonly frame = viewChild<ElementRef<HTMLElement>>('frame');
  private readonly columns = viewChild<ElementRef<HTMLElement>>('columns');

  private load: Subscription | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private swipeStart: { x: number; y: number; id: number } | null = null;

  constructor() {
    effect(() => {
      const request = this.request();
      this.load?.unsubscribe();
      this.passage.set(null);
      this.page.set(0);
      this.pageCount.set(1);
      this.selection.set('');
      if (!request) return;

      ensureLibron();
      this.state.set('loading');
      this.load = this.notes.getPassage(request.noteId).subscribe({
        next: (passage) => {
          this.passage.set(passage);
          this.state.set('ready');
        },
        error: () => this.state.set('error'),
      });
    });

    // Measure once the text is on the page, and whenever the frame resizes.
    effect(() => {
      const frame = this.frame()?.nativeElement;
      this.body();
      this.resizeObserver?.disconnect();
      if (!frame) return;
      this.resizeObserver = new ResizeObserver(() => this.zone.run(() => this.measure()));
      this.resizeObserver.observe(frame);
      queueMicrotask(() => this.measure());
      document.fonts?.ready.then(() => this.zone.run(() => this.measure()));
    });

    this.destroyRef.onDestroy(() => {
      this.load?.unsubscribe();
      this.resizeObserver?.disconnect();
    });
  }

  close(): void {
    this.viewer.close();
  }

  next(): void {
    this.page.update((page) => Math.min(page + 1, this.pageCount() - 1));
  }

  previous(): void {
    this.page.update((page) => Math.max(page - 1, 0));
  }

  openInBook(): void {
    const passage = this.passage();
    const request = this.request();
    if (!passage || !request) return;
    this.viewer.close();
    if (request.openInBook) {
      request.openInBook(passage);
      return;
    }
    const queryParams = passageSourceParams(passage);
    void this.router.navigate(['/read', passage.bookId], queryParams ? { queryParams } : undefined);
  }

  quote(): void {
    const passage = this.passage();
    const text = this.selection().trim();
    const quote = this.request()?.quoteSelection;
    if (!passage || !text || !quote) return;
    quote(text, passage);
    window.getSelection()?.removeAllRanges();
    this.selection.set('');
  }

  @HostListener('document:selectionchange')
  onSelectionChange(): void {
    if (!this.request()?.quoteSelection) return;
    const columns = this.columns()?.nativeElement;
    const selection = window.getSelection();
    if (!columns || !selection || selection.rangeCount === 0 || selection.isCollapsed) {
      this.selection.set('');
      return;
    }
    const range = selection.getRangeAt(0);
    this.selection.set(columns.contains(range.commonAncestorContainer) ? selection.toString() : '');
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!this.request() || this.state() !== 'ready') return;
    const target = event.target as HTMLElement | null;
    if (target?.closest('input, textarea, [contenteditable="true"]')) return;
    if (event.key === 'ArrowRight' || event.key === 'PageDown' || (event.key === ' ' && !event.shiftKey)) {
      event.preventDefault();
      this.next();
    } else if (event.key === 'ArrowLeft' || event.key === 'PageUp' || (event.key === ' ' && event.shiftKey)) {
      event.preventDefault();
      this.previous();
    } else if (event.key === 'Home') {
      event.preventDefault();
      this.page.set(0);
    } else if (event.key === 'End') {
      event.preventDefault();
      this.page.set(this.pageCount() - 1);
    }
  }

  onPointerDown(event: PointerEvent): void {
    if (event.pointerType === 'mouse') return;
    this.swipeStart = { x: event.clientX, y: event.clientY, id: event.pointerId };
  }

  onPointerUp(event: PointerEvent): void {
    const start = this.swipeStart;
    this.swipeStart = null;
    if (!start || start.id !== event.pointerId) return;
    const dx = event.clientX - start.x;
    const dy = event.clientY - start.y;
    // A swipe is mostly sideways; a selection drag or a scroll is not.
    if (Math.abs(dx) < SWIPE_DISTANCE || Math.abs(dx) < Math.abs(dy) * 1.5) return;
    if (window.getSelection()?.isCollapsed === false) return;
    if (dx < 0) this.next();
    else this.previous();
  }

  /** Page count from the column strip's width; keeps the reader's place. */
  private measure(): void {
    const frame = this.frame()?.nativeElement;
    const columns = this.columns()?.nativeElement;
    if (!frame || !columns) return;

    const width = frame.clientWidth;
    if (width <= 0) return;
    const fraction = this.pageCount() > 1 ? this.page() / (this.pageCount() - 1) : 0;
    if (width !== this.pageWidth()) this.pageWidth.set(width);

    // The strip's own width is one page; its scrollWidth reaches the last column.
    const count = Math.max(1, Math.round((columns.scrollWidth + PAGE_GAP) / (width + PAGE_GAP)));
    if (count !== this.pageCount()) {
      this.pageCount.set(count);
      this.page.set(Math.min(count - 1, Math.round(fraction * (count - 1))));
    }
  }
}

/** Libron is otherwise declared only inside the reader's book frames. */
function ensureLibron(): void {
  if (libronInjected || typeof document === 'undefined') return;
  libronInjected = true;
  const style = document.createElement('style');
  style.dataset['nostos'] = 'libron';
  style.textContent = libronFontFaceCss(document.baseURI);
  document.head.appendChild(style);
}
