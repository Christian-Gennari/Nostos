import { signal } from '@angular/core';
import type { Book, Contents, Rendition } from 'epubjs';
import type { Observable, Subscription } from 'rxjs';

import type { ReaderSourceTarget } from '../reader.interface';
import {
  rangeAtNormalizedResourceOffset,
  rangeForNormalizedResourceSpan,
  resolveGroundedEpubResourceHref,
  sentenceSpanInResource,
  type EpubSpineSource,
} from './epub-grounded-source';

/**
 * Quiet window used to decide that epub.js has finished re-laying-out after a
 * grounded display.
 */
export const GROUNDED_SETTLE_QUIET_MS = 400;

/**
 * Hard ceiling for one settle wait. `resized` events restart the quiet window,
 * so a stream of resizes must still reach a verdict rather than holding
 * progress persistence closed for ever.
 */
export const GROUNDED_SETTLE_MAX_MS = 2000;

/**
 * How long the passage just marked keeps the page after Save or Cancel (#1270).
 */
export const SELECTION_ANCHOR_GRACE_MS = 1000;

/** How a protected navigation ended: kept on screen, given up, or replaced. */
export type GroundedOutcome = 'confirmed' | 'failed' | 'superseded';

interface SelectionAnchorState {
  cfi: string;
  released: boolean;
  releaseTimer: ReturnType<typeof setTimeout> | null;
  settling: number | null;
}

interface GroundedNavigationState {
  generation: number;
  cfi: string;
  href: string | null;
  offset: number | null;
  markSentence: boolean;
  reasserted: boolean;
  layoutWidth: number | null;
  reassertedAfterReflow: boolean;
  cancelSettle: (() => void) | null;
  restore: boolean;
  onEnd: ((outcome: GroundedOutcome) => void) | null;
}

export interface EpubGroundedNavigatorHost {
  rendition(): Rendition | null;
  book(): Book | null;
  renderedContents(): Contents[];
  readingViewRect(): DOMRect | null;
  contentMatchesHref(content: Contents, href: string | null): boolean;
  spineSources(): EpubSpineSource[];
  currentLocation(): string | null;
  progressUnlocked(): boolean;
  pendingSelectionStart(): string | null;
  resizeEvents$: Observable<void>;
  releasePageHold(): void;
  clearLandingMark(): void;
  showLandingMark(cfi: string): void;
  onConfirmed(cfi: string): void;
}

/**
 * Orchestrates grounded source navigation, epub.js layout settle protection
 * (#582), saved-position restore (#1271), and selection anchor re-anchoring (#1270).
 */
export class EpubGroundedNavigator {
  readonly message = signal<string | null>(null);

  private navigationGeneration = 0;
  private groundedNavigation: GroundedNavigationState | null = null;
  private selectionAnchor: SelectionAnchorState | null = null;
  private reanchoring = false;
  private pendingSource: ReaderSourceTarget | null = null;
  private cancelFontWait: (() => void) | null = null;
  private destroyed = false;

  constructor(private readonly host: EpubGroundedNavigatorHost) {}

  hasPendingSource(): boolean {
    return this.pendingSource !== null;
  }

  consumePendingSource(): ReaderSourceTarget | null {
    const target = this.pendingSource;
    this.pendingSource = null;
    return target;
  }

  isProtectingProgress(): boolean {
    return this.groundedNavigation !== null || this.selectionAnchor !== null;
  }

  /**
   * Start a navigation operation. Bumps the generation and cancels any
   * grounded protection: a new navigation — including the reader's own page
   * turn, keyboard or TOC — must never be fought by a delayed reassert.
   */
  beginNavigation(): number {
    this.host.releasePageHold();
    if (!this.reanchoring) this.dropSelectionAnchor();
    this.navigationGeneration++;
    this.cancelGroundedNavigation();
    this.host.clearLandingMark();
    return this.navigationGeneration;
  }

  resetForBookLoad(): void {
    this.pendingSource = null;
    this.message.set(null);
    this.beginNavigation();
  }

  clearMessage(): void {
    this.message.set(null);
  }

  failSourceNavigation(error?: unknown): void {
    this.message.set('Could not locate this passage in the EPUB.');
    if (error !== undefined) {
      console.warn('Could not navigate to the grounded EPUB source:', error);
    }
  }

  async goToSource(target: ReaderSourceTarget): Promise<void> {
    if (target.type !== 'epub') return;

    this.beginNavigation();
    this.message.set(null);

    if (!this.host.rendition() || !this.host.progressUnlocked()) {
      this.pendingSource = target;
      return;
    }

    await this.applySource(target);
  }

  async applySource(target: ReaderSourceTarget): Promise<void> {
    const rendition = this.host.rendition();
    if (target.type !== 'epub' || !rendition || this.destroyed) return;

    const generation = this.beginNavigation();
    this.message.set(null);

    if (target.epubCfi) {
      this.protectGroundedTarget(
        generation,
        target.epubCfi,
        target.epubResourceHref ?? null,
        target.epubTextOffset ?? null,
        target.markSentence === true,
      );
      try {
        await rendition.display(target.epubCfi);
        if (this.destroyed || generation !== this.navigationGeneration) return;
        this.watchGroundedSettle(generation);
        return;
      } catch {
        this.cancelGroundedNavigation(generation);
      }
      if (this.destroyed || generation !== this.navigationGeneration) return;
    }

    if (!target.epubResourceHref) {
      this.failSourceNavigation();
      return;
    }

    let displayedHref = target.epubResourceHref;
    try {
      await rendition.display(displayedHref);
    } catch (exactError) {
      const compatibleHref = resolveGroundedEpubResourceHref(
        target.epubResourceHref,
        target.epubSpineIndex,
        this.host.spineSources(),
      );

      if (!compatibleHref || compatibleHref === displayedHref) {
        this.failSourceNavigation(exactError);
        return;
      }

      displayedHref = compatibleHref;
      try {
        await rendition.display(displayedHref);
      } catch (compatibilityError) {
        this.failSourceNavigation(compatibilityError);
        return;
      }
    }
    if (this.destroyed || generation !== this.navigationGeneration) return;

    if (target.epubTextOffset === null || target.epubTextOffset === undefined) return;

    let cfi: string;
    try {
      const contents = this.host.renderedContents();
      const content =
        contents.find((candidate) => this.host.contentMatchesHref(candidate, displayedHref)) ??
        contents[0];

      if (!content?.document) {
        this.failSourceNavigation();
        return;
      }

      const range = rangeAtNormalizedResourceOffset(
        content.document,
        Math.max(0, target.epubTextOffset),
      );
      if (!range) {
        this.failSourceNavigation();
        return;
      }

      const computed = (content as any).cfiFromRange?.(range);
      if (typeof computed !== 'string' || computed.length === 0) {
        this.failSourceNavigation();
        return;
      }
      cfi = computed;
    } catch (error) {
      this.failSourceNavigation(error);
      return;
    }
    if (this.destroyed || generation !== this.navigationGeneration) return;

    this.protectGroundedTarget(
      generation,
      cfi,
      displayedHref,
      target.epubTextOffset,
      target.markSentence === true,
    );
    try {
      await rendition.display(cfi);
    } catch (error) {
      this.cancelGroundedNavigation(generation);
      this.failSourceNavigation(error);
      return;
    }
    if (this.destroyed || generation !== this.navigationGeneration) return;
    this.watchGroundedSettle(generation);
  }

  /** Hold the page under the passage just captured (#1270). */
  holdSelectionAnchor(): void {
    const start = this.host.pendingSelectionStart();
    if (!start) return;
    const current = this.selectionAnchor;
    if (current) {
      if (current.releaseTimer) clearTimeout(current.releaseTimer);
      current.releaseTimer = null;
      current.released = false;
      current.cfi = start;
      return;
    }
    this.selectionAnchor = { cfi: start, released: false, releaseTimer: null, settling: null };
  }

  /** The passage was saved or dropped; keep the anchor through keyboard-close reflow. */
  releaseSelectionAnchor(): void {
    const anchor = this.selectionAnchor;
    if (!anchor || anchor.released) return;
    anchor.released = true;
    this.endSelectionAnchorAfterGrace(anchor);
  }

  reanchorOnResize(): void {
    if (this.selectionAnchor) {
      void this.reanchorOnSelection(this.selectionAnchor);
    }
  }

  /**
   * Open at the saved reading position and keep it there (#1271).
   */
  async restoreSavedPosition(rendition: Rendition, cfi: string): Promise<GroundedOutcome> {
    const generation = this.beginNavigation();
    const outcome = new Promise<GroundedOutcome>((resolve) => {
      this.protectGroundedTarget(generation, cfi, this.spineHrefForCfi(cfi), null, false, {
        onEnd: resolve,
      });
    });

    try {
      await rendition.display(cfi);
    } catch (error) {
      this.failGroundedNavigation(generation, error);
      return outcome;
    }
    if (this.destroyed || generation !== this.navigationGeneration) return outcome;
    this.watchGroundedSettle(generation);
    return outcome;
  }

  /**
   * Whether the range is inside the reading view, measured in the parent
   * document's viewport. Null when there is no layout to measure.
   */
  isRangeVisible(range: Range, content: Contents): boolean | null {
    if (typeof range?.getBoundingClientRect !== 'function') return null;

    const frame = content?.window?.frameElement;
    if (!frame || typeof frame.getBoundingClientRect !== 'function') return null;

    const view = this.host.readingViewRect();
    if (!view) return null;

    const rangeRect = range.getBoundingClientRect();
    const frameRect = frame.getBoundingClientRect();
    if (!rangeRect || !frameRect) return null;
    const left = frameRect.left + rangeRect.left;
    const right = frameRect.left + rangeRect.right;
    const top = frameRect.top + rangeRect.top;
    const bottom = frameRect.top + rangeRect.bottom;

    return right >= view.left && left <= view.right && bottom >= view.top && top <= view.bottom;
  }

  destroy(): void {
    this.destroyed = true;
    this.pendingSource = null;
    this.cancelGroundedNavigation();
    this.dropSelectionAnchor();
  }

  private endSelectionAnchorAfterGrace(anchor: SelectionAnchorState): void {
    if (anchor.releaseTimer) clearTimeout(anchor.releaseTimer);
    anchor.releaseTimer = setTimeout(() => {
      anchor.releaseTimer = null;
      if (this.selectionAnchor === anchor && anchor.settling === null) this.selectionAnchor = null;
    }, SELECTION_ANCHOR_GRACE_MS);
  }

  private dropSelectionAnchor(): void {
    const anchor = this.selectionAnchor;
    if (!anchor) return;
    if (anchor.releaseTimer) clearTimeout(anchor.releaseTimer);
    this.selectionAnchor = null;
  }

  private async reanchorOnSelection(anchor: SelectionAnchorState): Promise<void> {
    const rendition = this.host.rendition();
    if (!rendition || this.destroyed) return;

    this.reanchoring = true;
    let generation: number;
    try {
      generation = this.beginNavigation();
    } finally {
      this.reanchoring = false;
    }
    anchor.settling = generation;
    this.protectGroundedTarget(generation, anchor.cfi, this.spineHrefForCfi(anchor.cfi), null, false, {
      onEnd: (outcome) => {
        if (anchor.settling !== generation) return;
        anchor.settling = null;
        if (outcome === 'superseded') return;
        if (anchor.released && this.selectionAnchor === anchor) {
          this.endSelectionAnchorAfterGrace(anchor);
        }
      },
    });

    try {
      await rendition.display(anchor.cfi);
    } catch (error) {
      this.failGroundedNavigation(generation, error);
      return;
    }
    if (this.destroyed || generation !== this.navigationGeneration) return;
    this.watchGroundedSettle(generation);
  }

  private protectGroundedTarget(
    generation: number,
    cfi: string,
    href: string | null,
    offset: number | null,
    markSentence = false,
    restore: { onEnd: (outcome: GroundedOutcome) => void } | null = null,
  ): void {
    this.groundedNavigation = {
      generation,
      cfi,
      href,
      offset,
      markSentence,
      reasserted: false,
      layoutWidth: this.groundedLayoutWidth(href),
      reassertedAfterReflow: false,
      cancelSettle: null,
      restore: restore !== null,
      onEnd: restore?.onEnd ?? null,
    };
  }

  private cancelGroundedNavigation(generation?: number): void {
    const target = this.groundedNavigation;
    if (!target || (generation !== undefined && target.generation !== generation)) return;
    this.groundedNavigation = null;
    target.cancelSettle?.();
    this.cancelFontWait?.();
    target.onEnd?.('superseded');
  }

  private watchGroundedSettle(generation: number): void {
    const target = this.groundedNavigation;
    const rendition = this.host.rendition();
    if (
      this.destroyed ||
      !target ||
      target.generation !== generation ||
      generation !== this.navigationGeneration ||
      !rendition
    ) {
      return;
    }

    let quietTimer: ReturnType<typeof setTimeout> | null = null;
    let deadlineTimer: ReturnType<typeof setTimeout> | null = null;
    let resizeSubscription: Subscription | null = null;

    const cleanup = () => {
      if (quietTimer) {
        clearTimeout(quietTimer);
        quietTimer = null;
      }
      if (deadlineTimer) {
        clearTimeout(deadlineTimer);
        deadlineTimer = null;
      }
      resizeSubscription?.unsubscribe();
      resizeSubscription = null;
      rendition.off('resized', onResized);
      const current = this.groundedNavigation;
      if (current && current.generation === generation) {
        current.cancelSettle = null;
      }
    };

    const finish = () => {
      cleanup();
      void this.afterSectionFonts(generation).then(() => this.verifyGroundedNavigation(generation));
    };

    const restartQuietWindow = () => {
      if (quietTimer) clearTimeout(quietTimer);
      quietTimer = setTimeout(finish, GROUNDED_SETTLE_QUIET_MS);
    };

    const onResized = () => restartQuietWindow();

    target.cancelSettle = cleanup;
    rendition.on('resized', onResized);
    resizeSubscription = this.host.resizeEvents$.subscribe(restartQuietWindow);
    deadlineTimer = setTimeout(finish, GROUNDED_SETTLE_MAX_MS);
    restartQuietWindow();
  }

  private async afterSectionFonts(generation: number): Promise<void> {
    if (this.destroyed || generation !== this.navigationGeneration) return;
    const loading: Promise<unknown>[] = [];
    for (const content of this.host.renderedContents()) {
      const fonts = content?.document?.fonts;
      if (fonts && fonts.status === 'loading') loading.push(fonts.ready);
    }
    if (loading.length === 0) return;

    let timer: ReturnType<typeof setTimeout> | undefined;
    let resolveCancel: (() => void) | null = null;
    const cancelPromise = new Promise<void>((resolve) => {
      resolveCancel = resolve;
    });
    const cancel = () => resolveCancel?.();
    this.cancelFontWait = cancel;

    const deadline = new Promise<void>((resolve) => {
      timer = setTimeout(resolve, GROUNDED_SETTLE_MAX_MS);
    });
    try {
      await Promise.race([Promise.all(loading).catch(() => undefined), deadline, cancelPromise]);
    } finally {
      if (timer !== undefined) clearTimeout(timer);
      if (this.cancelFontWait === cancel) this.cancelFontWait = null;
    }
  }

  private async verifyGroundedNavigation(generation: number): Promise<void> {
    const target = this.groundedNavigation;
    if (
      this.destroyed ||
      !target ||
      target.generation !== generation ||
      generation !== this.navigationGeneration
    ) {
      return;
    }

    if (this.isGroundedTargetOnPage()) {
      this.confirmGroundedNavigation(generation);
      return;
    }

    if (target.reasserted) {
      const width = this.groundedLayoutWidth(target.href);
      const laidOutAgain =
        width !== null && target.layoutWidth !== null && width !== target.layoutWidth;

      if (target.reassertedAfterReflow || !laidOutAgain) {
        this.failGroundedNavigation(generation);
        return;
      }
      target.reassertedAfterReflow = true;
      target.layoutWidth = width;
    }
    target.reasserted = true;

    const rendition = this.host.rendition();
    if (!rendition) {
      this.failGroundedNavigation(generation);
      return;
    }

    this.host.releasePageHold();
    try {
      await rendition.display(target.cfi);
    } catch (error) {
      this.failGroundedNavigation(generation, error);
      return;
    }
    if (this.destroyed || generation !== this.navigationGeneration) return;
    this.watchGroundedSettle(generation);
  }

  private groundedLayoutWidth(href: string | null): number | null {
    try {
      const rendered = this.host.renderedContents().filter((content) => !!content?.document);
      const content =
        (href ? rendered.find((candidate) => this.host.contentMatchesHref(candidate, href)) : null) ??
        rendered[0];
      const frame = content?.window?.frameElement;
      if (!frame || typeof frame.getBoundingClientRect !== 'function') return null;

      const width = Math.round(frame.getBoundingClientRect().width);
      return width > 0 ? width : null;
    } catch {
      return null;
    }
  }

  private confirmGroundedNavigation(generation: number): void {
    const target = this.groundedNavigation;
    if (!target || target.generation !== generation) return;

    this.groundedNavigation = null;
    target.cancelSettle?.();
    this.host.onConfirmed(target.cfi);
    if (target.markSentence) this.markLandingSentence(target.href, target.offset);
    target.onEnd?.('confirmed');
  }

  private markLandingSentence(href: string | null, offset: number | null): void {
    if (offset === null) return;

    try {
      const rendered = this.host.renderedContents().filter((content) => !!content?.document);
      const contents =
        (href ? rendered.find((candidate) => this.host.contentMatchesHref(candidate, href)) : null) ??
        rendered[0];
      if (!contents?.document) return;

      const span = sentenceSpanInResource(contents.document, offset);
      if (!span) return;

      const range = rangeForNormalizedResourceSpan(contents.document, span.start, span.length);
      const cfi = range ? contents.cfiFromRange(range) : null;
      if (typeof cfi !== 'string' || cfi.length === 0) return;

      this.host.showLandingMark(cfi);
    } catch (error) {
      console.warn('Could not mark the landing sentence:', error);
    }
  }

  private failGroundedNavigation(generation: number, error?: unknown): void {
    const target = this.groundedNavigation;
    if (!target || target.generation !== generation) return;

    this.groundedNavigation = null;
    target.cancelSettle?.();
    if (target.restore) {
      console.warn('Could not keep the saved reading position on screen:', error ?? target.cfi);
    } else {
      this.failSourceNavigation(error);
    }
    target.onEnd?.('failed');
  }

  private spineHrefForCfi(cfi: string): string | null {
    try {
      const href = (this.host.book()?.spine as any)?.get?.(cfi)?.href;
      return typeof href === 'string' && href.length > 0 ? href : null;
    } catch {
      return null;
    }
  }

  private isGroundedTargetOnPage(): boolean {
    const target = this.groundedNavigation;
    if (!target) return false;

    const found = this.groundedTargetRange();
    if (found) {
      const visible = this.isRangeVisible(found.range, found.content);
      if (visible !== null) return visible;
    }

    const current = this.host.currentLocation();
    return current !== null && current === target.cfi;
  }

  private groundedTargetRange(): { content: Contents; range: Range } | null {
    const target = this.groundedNavigation;
    if (!target) return null;

    const rendered = this.host.renderedContents().filter((content) => !!content?.document);
    if (rendered.length === 0) return null;

    const matching = rendered.filter((content) =>
      this.host.contentMatchesHref(content, target.href),
    );
    const candidates = matching.length > 0 ? matching : rendered;

    for (const content of candidates) {
      if (target.offset !== null) {
        const range = rangeAtNormalizedResourceOffset(
          content.document,
          Math.max(0, target.offset),
        );
        if (range) return { content, range };
      }

      if (target.cfi && typeof (content as any).range === 'function') {
        try {
          const range = (content as any).range(target.cfi) as Range | null;
          if (range) return { content, range };
        } catch {
          // The CFI does not resolve in this contents — try the next one.
        }
      }
    }
    return null;
  }
}
