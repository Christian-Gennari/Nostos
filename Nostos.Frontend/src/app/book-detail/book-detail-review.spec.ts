/**
 * Long-review collapse on Book Details (issue #159).
 *
 * jsdom implements no layout: every element reports `scrollHeight = 0` and the
 * computed `line-height` is never filled in from a stylesheet. So the collapse
 * decision cannot be exercised here against a real engine, and these tests do not
 * pretend to. They drive the two inputs the component actually reads — the
 * rendered content height and the computed line height — and assert the
 * DECISION, the MARKUP and the STATE TRANSITIONS.
 *
 * The geometry itself (how many lines a given review occupies at a given width,
 * and that a clamped paragraph still measures its full height) is measured in a
 * real browser: e2e/book-detail-review.spec.ts.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { BookDetail, REVIEW_COLLAPSE_LINES } from './book-detail.component';
import { Book } from '../core/dtos/book.dtos';
import { ToastService } from '../core/services/toast.service';

/** Matches the component stylesheet (`line-height: 1.6` at `font-size: 1rem`). */
const LINE_HEIGHT_PX = 25.6;

const SHORT_REVIEW = 'A short, ordinary review that should never collapse.';
const LONG_REVIEW = 'A deliberately long review. '.repeat(120).trim();

/** The rendered line count the paragraph pretends to occupy. */
let renderedLines = 4;
/** The review column width the layout pretends to have. */
let columnWidth = 718;

/** Captured ResizeObserver callbacks, so a resize can be driven deterministically. */
let resizeCallbacks: Array<() => void> = [];

const originalDescriptors = {
  scrollHeight: Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight'),
  clientWidth: Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientWidth'),
};

function installLayoutStubs(): void {
  // `scrollHeight` is the component's source of truth for the review's full
  // height — including while the paragraph is clamped.
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get(this: HTMLElement) {
      return this.classList?.contains('review-text') ? Math.round(renderedLines * LINE_HEIGHT_PX) : 0;
    },
  });
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
    configurable: true,
    get: () => columnWidth,
  });

  const real = window.getComputedStyle.bind(window);
  vi.spyOn(window, 'getComputedStyle').mockImplementation((el: Element) => {
    const cs = real(el);
    // Override only the two metrics the component reads; everything else must
    // stay real so Angular's own rendering is unaffected.
    return new Proxy(cs, {
      get: (target, prop) =>
        prop === 'lineHeight'
          ? `${LINE_HEIGHT_PX}px`
          : prop === 'fontSize'
            ? '16px'
            : Reflect.get(target, prop as string, target),
    }) as CSSStyleDeclaration;
  });

  // jsdom ships no ResizeObserver; the component checks for it before observing.
  resizeCallbacks = [];
  class FakeResizeObserver {
    constructor(cb: () => void) {
      resizeCallbacks.push(cb);
    }
    observe(): void {}
    disconnect(): void {}
    unobserve(): void {}
  }
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeResizeObserver;
}

function restoreLayoutStubs(): void {
  vi.restoreAllMocks();
  resizeCallbacks = [];
  delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver;
  for (const [name, descriptor] of Object.entries(originalDescriptors)) {
    if (descriptor) Object.defineProperty(HTMLElement.prototype, name, descriptor);
    else delete (HTMLElement.prototype as unknown as Record<string, unknown>)[name];
  }
}

const book: Book = {
  id: 'b1',
  title: 'Meditations',
  subtitle: null,
  author: 'Marcus Aurelius',
  editor: null,
  translator: null,
  narrator: null,
  description: null,
  type: 'physical',
  edition: null,
  asin: null,
  duration: null,
  isbn: null,
  publisher: null,
  placeOfPublication: null,
  publishedDate: null,
  pageCount: null,
  language: null,
  categories: null,
  series: null,
  volumeNumber: null,
  createdAt: '2026-08-01T08:00:00+02:00',
  hasFile: false,
  fileName: null,
  coverUrl: null,
  collectionIds: [],
  lastLocation: null,
  progressPercent: 0,
  lastReadAt: null,
  rating: 0,
  isFavorite: false,
  personalReview: null,
  finishedAt: null,
};

describe('Book Details — long review collapse (issue #159)', () => {
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;

  async function setup(review: string | null, description: string | null = null): Promise<void> {
    fixture = TestBed.createComponent(BookDetail);
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush({ ...book, personalReview: review, description });
    httpMock.expectOne('/api/books/b1/notes').flush([]);
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match('/api/topics').forEach((r) => r.flush([]));

    // Settle the `afterNextRender` measurement pass and the render it triggers.
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const reviewEl = (): HTMLElement | null => fixture.nativeElement.querySelector('.review-text');
  const bodyEl = (): HTMLElement | null => fixture.nativeElement.querySelector('.review-body');
  const toggleEl = (): HTMLButtonElement | null =>
    fixture.nativeElement.querySelector('.review-toggle');
  const fadeEl = (): HTMLElement | null => fixture.nativeElement.querySelector('.review-fade');

  beforeEach(async () => {
    renderedLines = 4;
    columnWidth = 718;
    installLayoutStubs();
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: book.id })) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(ToastService);
  });

  afterEach(() => {
    httpMock.match('/api/topics').forEach((r) => r.flush([]));
    httpMock.verify();
    restoreLayoutStubs();
  });

  it('keeps a normal-length review fully expanded with no control', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES - 4;
    await setup(SHORT_REVIEW);

    expect(reviewEl()?.textContent).toBe(SHORT_REVIEW);
    expect(bodyEl()?.classList.contains('clamped')).toBe(false);
    expect(toggleEl()).toBeNull();
    expect(fadeEl()).toBeNull();
  });

  it('does not collapse a review exactly at the threshold', async () => {
    // The threshold is "strictly more than", so the boundary itself stays open.
    renderedLines = REVIEW_COLLAPSE_LINES;
    await setup(LONG_REVIEW);

    expect(bodyEl()?.classList.contains('clamped')).toBe(false);
    expect(toggleEl()).toBeNull();
  });

  it('collapses a review past the threshold and offers an explicit control', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 1;
    await setup(LONG_REVIEW);

    expect(bodyEl()?.classList.contains('clamped')).toBe(true);
    expect(toggleEl()?.textContent).toContain('Read full review');
    expect(fadeEl()).toBeTruthy();
  });

  it('exposes the collapsed state and the controlled element for assistive tech', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW);

    const toggle = toggleEl()!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-controls')).toBe(reviewEl()!.id);
    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle.getAttribute('type')).toBe('button');
  });

  it('expands the full review on demand and collapses it again', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW);

    // Expand.
    toggleEl()!.click();
    fixture.detectChanges();
    expect(bodyEl()?.classList.contains('clamped')).toBe(false);
    expect(toggleEl()?.getAttribute('aria-expanded')).toBe('true');
    expect(toggleEl()?.textContent).toContain('Show less');
    expect(fadeEl()).toBeNull();

    // Collapse again, in place — no navigation, no reload.
    toggleEl()!.click();
    fixture.detectChanges();
    expect(bodyEl()?.classList.contains('clamped')).toBe(true);
    expect(toggleEl()?.getAttribute('aria-expanded')).toBe('false');
  });

  it('never truncates or rewrites the stored review when expanding or collapsing', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW);

    const expandedText = reviewEl()!.textContent;
    expect(expandedText).toBe(LONG_REVIEW);

    toggleEl()!.click();
    fixture.detectChanges();
    // The DOM keeps the whole review in the collapsed state too — the clamp is
    // presentation only, so nothing is sliced out of it.
    expect(reviewEl()!.textContent).toBe(LONG_REVIEW);
    expect(reviewEl()!.textContent).toBe(expandedText);
    // And no write is attempted: the toggle is local state, not persistence.
    httpMock.expectNone((req) => req.method === 'PUT' || req.method === 'PATCH');
  });

  it('re-decides when the column narrows past the threshold', async () => {
    // Fits comfortably at the desktop width...
    renderedLines = REVIEW_COLLAPSE_LINES - 10;
    await setup(LONG_REVIEW);
    expect(toggleEl()).toBeNull();

    // ...then the column narrows, so the same text needs more lines.
    expect(resizeCallbacks.length).toBeGreaterThan(0);
    renderedLines = REVIEW_COLLAPSE_LINES + 10;
    columnWidth = 320;
    resizeCallbacks.forEach((cb) => cb());
    fixture.detectChanges();

    expect(bodyEl()?.classList.contains('clamped')).toBe(true);
    expect(toggleEl()?.textContent).toContain('Read full review');
  });

  it('needs no control at all when the book has no review', async () => {
    renderedLines = 0;
    await setup(null);

    expect(reviewEl()).toBeNull();
    expect(toggleEl()).toBeNull();
  });
});

describe('Book Details — coordinated disclosure affordance (#159 follow-up)', () => {
  let fixture: ComponentFixture<BookDetail>;
  let httpMock: HttpTestingController;

  async function setup(review: string | null, description: string | null): Promise<void> {
    fixture = TestBed.createComponent(BookDetail);
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne('/api/books/b1').flush({ ...book, personalReview: review, description });
    httpMock.expectOne('/api/books/b1/notes').flush([]);
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match('/api/topics').forEach((r) => r.flush([]));

    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const allToggles = (): HTMLButtonElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll('.expand-btn')) as HTMLButtonElement[];

  beforeEach(async () => {
    renderedLines = 4;
    columnWidth = 718;
    installLayoutStubs();
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ id: book.id })) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(ToastService);
  });

  afterEach(() => {
    httpMock.match('/api/topics').forEach((r) => r.flush([]));
    httpMock.verify();
    restoreLayoutStubs();
  });

  it('gives both collapsible blocks the same control, told apart by their labels', async () => {
    // Both blocks collapse at once, which is the state the user objected to:
    // two identical buttons stacked in one card.
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW, LONG_REVIEW);

    const toggles = allToggles();
    expect(toggles.length).toBe(2);

    // Same affordance — one shared class, not two components that each
    // invented their own disclosure control.
    for (const t of toggles) {
      expect(t.classList.contains('expand-btn')).toBe(true);
    }

    // Told apart by what they reveal rather than by two generic labels.
    const [reviewToggle, synopsisToggle] = toggles;
    expect(reviewToggle.textContent).toContain('Read full review');
    expect(synopsisToggle.textContent).toContain('Read full synopsis');

    // Neither falls back to a bare generic verb.
    for (const t of toggles) {
      const label = (t.textContent || '').trim();
      expect(label).not.toBe('Read More');
      expect(label).not.toBe('Show More');
    }

    // Both expose their state AND the element they control. The synopsis
    // control previously had neither attribute, so it announced nothing.
    for (const t of toggles) {
      expect(['true', 'false']).toContain(t.getAttribute('aria-expanded'));
      const controlled = t.getAttribute('aria-controls');
      expect(controlled).toBeTruthy();
      expect(fixture.nativeElement.querySelector(`#${controlled}`)).toBeTruthy();
    }
  });

  it('rotates one chevron glyph instead of swapping two different glyphs', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW, null);

    const iconBefore = allToggles()[0].querySelector('.icon') as HTMLElement;
    expect(iconBefore.textContent?.trim()).toBe('↓');
    expect(iconBefore.classList.contains('rotated')).toBe(false);
    // Decorative: the label already says what happens.
    expect(iconBefore.getAttribute('aria-hidden')).toBe('true');

    allToggles()[0].click();
    fixture.detectChanges();

    const iconAfter = allToggles()[0].querySelector('.icon') as HTMLElement;
    expect(iconAfter.classList.contains('rotated')).toBe(true);
    // The glyph itself never changes — only its rotation, so it reads as one
    // control changing state rather than two different controls.
    expect(iconAfter.textContent?.trim()).toBe('↓');
  });

  it('keeps the two blocks independently expandable', async () => {
    renderedLines = REVIEW_COLLAPSE_LINES + 20;
    await setup(LONG_REVIEW, LONG_REVIEW);

    const [reviewToggle, synopsisToggle] = allToggles();
    reviewToggle.click();
    fixture.detectChanges();

    // Opening the review must not touch the synopsis: a reader cross-references
    // the premise against their own notes, so neither may close the other.
    expect(reviewToggle.getAttribute('aria-expanded')).toBe('true');
    expect(synopsisToggle.getAttribute('aria-expanded')).toBe('false');
  });
});
