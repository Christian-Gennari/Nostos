/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import type { ImportActivity } from '../../core/dtos/import.dtos';
import { ImportService } from '../../core/services/import.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';

/**
 * A book whose import failed says so where the book lives — on its card, on its
 * row — with the actions that can still be taken.
 *
 * The MESSAGE is the book row's own `statusMessage`, so it survives a reload and
 * needs no feed; the ACTIONS need the feed, which is the one place the provider
 * and item ids still exist. Both views drew this block by hand, so both got the
 * same copy and the same 44px touch targets from two stylesheets; it is one
 * component now, and it owns the one thing the parent sheets were carrying for it
 * (the cluster's `z-index` above the card's stretched link).
 */
@Component({
  selector: 'app-library-item-failure',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent],
  template: `
    <p class="item-failure">{{ message() }}</p>
    @if (failure(); as failing) {
      <div class="item-failure-actions">
        @if (failing.canRetry) {
          <button type="button" appButton variant="primary" size="sm" class="item-action" (click)="retry(failing, $event)">
            <nostos-icon name="arrow-counter-clockwise" [size]="13"></nostos-icon>
            <span>Try again</span>
          </button>
        }
        <button type="button" appButton variant="secondary" size="sm" class="item-action" (click)="dismiss(failing, $event)">
          <nostos-icon name="x" [size]="13"></nostos-icon>
          <span>Dismiss</span>
        </button>
      </div>
    }
  `,
  styleUrls: ['./library-item-failure.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryItemFailure {
  private readonly imports = inject(ImportService);

  /** The book's own explanation of the failure. */
  readonly message = input.required<string>();
  /** The feed's record of it, or undefined once the feed has forgotten the job. */
  readonly failure = input<ImportActivity | undefined>();

  retry(activity: ImportActivity, event: Event): void {
    event.stopPropagation();
    this.imports.retry(activity);
  }

  dismiss(activity: ImportActivity, event: Event): void {
    event.stopPropagation();
    this.imports.dismiss(activity);
  }
}
