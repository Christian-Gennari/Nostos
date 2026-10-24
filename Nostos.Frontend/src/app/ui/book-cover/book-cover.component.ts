/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { booleanAttribute, ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { BloomArtDirective } from '../bloom-art/bloom-art.directive';
import { NOSTOS_CONCEPTS } from '../icon/nostos-concepts';
import type { NostosIconName } from '../icon/nostos-icons';

/**
 * Fixed thumbnail rungs (2:3 unless the caller's class sets its own box), or
 * `fill` for a frame the surface already owns (Library grid card, Book detail).
 *
 *   xs   22×32   command palette row
 *   sm   32×46   Library list row
 *   md   36×54   Reading list "Someday" row, add-book results
 *   lg   84×126  Reading list "Next up" shelf
 */
export type BookCoverSize = 'xs' | 'sm' | 'md' | 'lg' | 'fill';

/** How the artwork arrives: as it decodes, a soft fade, or the Library's lens bloom. */
export type BookCoverReveal = 'none' | 'fade' | 'bloom';

export type BookCoverFormat = 'audiobook' | 'ebook' | 'pdf' | 'physical';

/**
 * The format a placeholder names, from whatever a surface knows about the book.
 * Null when nothing says (a reading-list wish, a discovery result).
 */
export function bookCoverFormat(
  book: { type?: string | null; format?: string | null; fileName?: string | null } | null | undefined,
): BookCoverFormat | null {
  if (!book) return null;
  const type = book.type?.toLowerCase() ?? '';
  const format = book.format?.trim().toLowerCase() ?? '';
  const extension = book.fileName?.split('.').pop()?.trim().toLowerCase() ?? '';
  if (type === 'audiobook' || type === 'audio' || format === 'audiobook' || format === 'audio') return 'audiobook';
  if (type === 'physical' || format === 'physical') return 'physical';
  if (type === 'pdf' || format === 'pdf' || extension === 'pdf') return 'pdf';
  if (type || format || extension) return 'ebook';
  return null;
}

const ICON_SIZE: Record<BookCoverSize, number> = { xs: 13, sm: 16, md: 18, lg: 28, fill: 48 };

/**
 * A book's cover, or — when it has none, or the image fails — one quiet
 * placeholder: the image ground with the book's format glyph, the same on every
 * surface. It used to be drawn six ways (a grey `#ccc` glyph, a hue-tinted cloth
 * tile, an empty box, a glyph tile…).
 *
 * The host is the cover's box. Surfaces that frame the cover themselves (a
 * card with badges and hover actions) use `size="fill"` inside that frame and
 * keep their own border and shadow; thumbnails take a rung and get a hairline.
 */
@Component({
  selector: 'nostos-book-cover',
  standalone: true,
  imports: [NostosIconComponent, BloomArtDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (showImage()) {
      @if (reveal() === 'bloom') {
        <img
          appBloomArt
          class="nostos-book-cover__img"
          [src]="src()"
          [alt]="altText()"
          [attr.loading]="loading()"
          decoding="async"
          (error)="onError()"
        />
      } @else {
        <img
          class="nostos-book-cover__img"
          [class.is-fading]="reveal() === 'fade'"
          [class.is-loaded]="loaded()"
          [src]="src()"
          [alt]="altText()"
          [attr.loading]="loading()"
          decoding="async"
          (load)="loaded.set(true)"
          (error)="onError()"
        />
      }
    } @else {
      <span
        class="nostos-book-cover__placeholder"
        [attr.role]="decorative() ? null : 'img'"
        [attr.aria-label]="decorative() ? null : altText()"
        [attr.aria-hidden]="decorative() ? 'true' : null"
      >
        <nostos-icon [name]="icon()" [size]="iconPx()" [weight]="size() === 'fill' || size() === 'lg' ? 'thin' : 'light'" />
      </span>
    }
  `,
  host: {
    class: 'nostos-book-cover',
    '[class.nostos-book-cover--xs]': "size() === 'xs'",
    '[class.nostos-book-cover--sm]': "size() === 'sm'",
    '[class.nostos-book-cover--md]': "size() === 'md'",
    '[class.nostos-book-cover--lg]': "size() === 'lg'",
    '[class.nostos-book-cover--fill]': "size() === 'fill'",
    '[class.is-placeholder]': '!showImage()',
  },
  styles: [
    `
      :host {
        display: block;
        flex: none;
        box-sizing: border-box;
        overflow: hidden;
        aspect-ratio: 2 / 3;
        background: var(--surface-image-ground);
        color: var(--color-text-faint);
        border-radius: var(--radius-sm);
        /* A hairline so a pale cover, or the empty ground, still has an edge. */
        border: 1px solid var(--border-color);
      }

      :host(.nostos-book-cover--xs) {
        width: 1.375rem;
        height: 2rem;
        border-radius: 3px;
      }
      :host(.nostos-book-cover--sm) {
        width: 32px;
        height: 46px;
      }
      :host(.nostos-book-cover--md) {
        width: 2.25rem;
        height: 3.375rem;
      }
      :host(.nostos-book-cover--lg) {
        width: 5.25rem;
        height: 7.875rem;
      }
      /* The surface's frame is the box, its border and its corner. */
      :host(.nostos-book-cover--fill) {
        width: 100%;
        height: 100%;
        aspect-ratio: auto;
        border: 0;
        border-radius: inherit;
        background: transparent;
      }

      .nostos-book-cover__img {
        display: block;
        width: 100%;
        height: 100%;
        object-fit: cover;
        image-rendering: high-quality;
        backface-visibility: hidden;
      }

      /* Bloom: while the reveal runs the art is its own composited layer, whose
         edge snaps to a device pixel; overscanning by 1px keeps the ground from
         showing as a hairline column inside the frame (see library.component.css). */
      .nostos-book-cover__img.bloom-art {
        width: calc(100% + 2px);
        height: calc(100% + 2px);
        margin: -1px;
        max-width: none;
        max-height: none;
      }

      .nostos-book-cover__img.is-fading {
        opacity: 0;
        transition: opacity var(--book-cover-fade, var(--motion-base)) var(--ease-out, ease-out);
      }
      .nostos-book-cover__img.is-fading.is-loaded {
        opacity: 1;
      }

      .nostos-book-cover__placeholder {
        display: flex;
        align-items: center;
        justify-content: center;
        width: 100%;
        height: 100%;
      }

      @media (prefers-reduced-motion: reduce) {
        .nostos-book-cover__img.is-fading {
          transition: none;
        }
      }
    `,
  ],
})
export class BookCoverComponent {
  /** The image URL; null or empty shows the placeholder. */
  readonly src = input<string | null | undefined>(null);
  /** The book's title, for the image's alternative text. */
  readonly bookTitle = input<string | null | undefined>(null);
  /** Overrides the alternative text built from the title. */
  readonly alt = input<string | null | undefined>(null);
  /** For a cover beside its own title: the image is announced as nothing. */
  readonly decorative = input(false, { transform: booleanAttribute });
  readonly size = input<BookCoverSize>('sm');
  /** The format the placeholder's glyph names; null is a book in general. */
  readonly format = input<BookCoverFormat | null | undefined>(null);
  readonly loading = input<'lazy' | 'eager'>('lazy');
  readonly reveal = input<BookCoverReveal>('none');
  /** Overrides the rung's glyph size. */
  readonly iconSize = input<number | null>(null);

  /** The URL that failed to load; a new URL gets its own chance. */
  private readonly failedSrc = signal<string | null>(null);
  protected readonly loaded = signal(false);

  protected readonly showImage = computed(() => {
    const src = this.src();
    return !!src && src !== this.failedSrc();
  });

  protected readonly altText = computed(() => {
    if (this.decorative()) return '';
    const alt = this.alt();
    if (alt != null) return alt;
    const title = this.bookTitle()?.trim();
    return title ? `${title} cover` : 'Book cover';
  });

  protected readonly icon = computed<NostosIconName>(() => {
    const format = this.format();
    return format ? NOSTOS_CONCEPTS[format] : 'book';
  });

  protected readonly iconPx = computed(() => this.iconSize() ?? ICON_SIZE[this.size()]);

  protected onError(): void {
    this.failedSrc.set(this.src() ?? null);
  }
}
