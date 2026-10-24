/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { booleanAttribute, ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { canObserveIntersections, InfiniteScrollDirective } from '../../core/directives/infinite-scroll.directive';
import { ButtonComponent } from '../button/button.component';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';

/**
 * The end of a paged list (issue #1202): the next page loads as the reader
 * scrolls toward it, with a small spinner while it comes, and an inline
 * "Try again" in its place when it fails. Library and Brain both end this way.
 *
 * A "Load more" button stays in the tab order for keyboards and screen
 * readers: hidden until it is focused while scrolling does the work, and
 * shown as a quiet button where the browser cannot watch the list end.
 *
 *   <nostos-paging-status
 *     [hasMore]="notes().length < total()"
 *     [loading]="loading()"
 *     [failed]="failed()"
 *     label="Loading more notes"
 *     failedMessage="More notes could not be loaded."
 *     (more)="loadMore()"
 *     (retry)="loadPage()"
 *   />
 */
@Component({
  selector: 'nostos-paging-status',
  standalone: true,
  imports: [ButtonComponent, EmptyStateComponent, InfiniteScrollDirective, LoadingIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (hasMore()) {
      <div
        class="nostos-paging-status__end"
        appInfiniteScroll
        [root]="root()"
        [rootMargin]="ROOT_MARGIN"
        [paused]="loading() || failed()"
        (reached)="more.emit()"
      >
        @if (failed()) {
          <!-- The same failure state as everywhere else, in its compact size. -->
          <nostos-empty-state
            class="nostos-paging-status__failed"
            compact
            error
            [message]="failedMessage()"
            (retry)="retry.emit()"
          />
        } @else {
          @if (loading()) {
            <nostos-loading-indicator [label]="label()" size="md" />
          }
          <!-- Stays while the page loads, so a keyboard's focus is not dropped. -->
          <button
            appButton
            variant="ghost"
            size="sm"
            type="button"
            class="nostos-paging-status__fallback"
            [class.visually-hidden-until-focus]="observing || loading()"
            (click)="more.emit()"
          >
            Load more
          </button>
        }
      </div>
    }
  `,
  host: { class: 'nostos-paging-status' },
  styles: [
    `
      :host {
        display: block;
        width: 100%;
      }

      .nostos-paging-status__end {
        display: flex;
        align-items: center;
        justify-content: center;
        min-height: 3rem;
        padding: 1.25rem 0;
      }

    `,
  ],
})
export class PagingStatusComponent {
  /** Whether there is a next page at all; nothing is drawn when there is not. */
  readonly hasMore = input(false, { transform: booleanAttribute });
  readonly loading = input(false, { transform: booleanAttribute });
  readonly failed = input(false, { transform: booleanAttribute });
  /** What the spinner announces. */
  readonly label = input('Loading more');
  readonly failedMessage = input('More could not be loaded.');
  /** The scrolling element the list lives in; null is the viewport. */
  readonly root = input<Element | null>(null);
  /** How far ahead of the list end the next page is asked for: one distance everywhere. */
  protected readonly ROOT_MARGIN = '600px 0px';

  /** The list end came into view, or the fallback was pressed. */
  readonly more = output<void>();
  /** "Try again" after a failed page. */
  readonly retry = output<void>();

  protected readonly observing = canObserveIntersections();
}
