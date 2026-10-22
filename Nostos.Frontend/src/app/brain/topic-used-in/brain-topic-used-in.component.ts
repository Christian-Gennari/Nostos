import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Router } from '@angular/router';

import type { TopicWritingDto, TopicWritingUsageDto } from '../../core/services/topics.service';
import { ChipComponent } from '../../ui/chip/chip.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';

/** "Used in" shows this many writings as pills, then a "+N" pill with the rest. */
export const USED_IN_SHOWN = 3;

/** How many writings the topic page asks for: the pills and the "+N" menu. */
export const USED_IN_FETCHED = 500;

/**
 * Where a topic is used in writing (issue #1162): a row under "Often with",
 * in that row's style. The writings that cite one of the topic's notes,
 * newest edited first, each opening in the Studio; past three, a "+N" pill
 * lists the rest in the shared menu. Read-only: a writing reaches a topic
 * only through what it cites. Hidden when no writing uses the topic.
 *
 * The host carries the parent's `often-with` row class, so the row's box,
 * gap and padding are the same rules; the rules below repeat that row's
 * label and resting-chip look, which do not cross into this view.
 */
@Component({
  selector: 'app-brain-topic-used-in',
  standalone: true,
  imports: [ChipComponent, ...NOSTOS_MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[attr.aria-label]': "shown().length ? 'Used in' : null",
    '[attr.role]': "shown().length ? 'group' : null",
    '[class.is-empty]': '!shown().length',
    'data-testid': 'topic-used-in',
  },
  styles: [
    `
      :host(.is-empty) {
        display: none !important;
      }

      .often-label {
        margin-right: 0.25rem;
        color: var(--color-text-light);
        font-size: 0.8rem;
      }

      /* An "Often with" chip at rest (its aria-pressed="false" look): these
         open a writing rather than toggle a filter, so they carry no
         pressed state, only the same quiet look. */
      .often-chip {
        border-color: var(--border-color);
        background: transparent;
        color: var(--color-text-light);
      }

      .often-chip:hover:not(:disabled),
      .often-chip[aria-expanded='true'] {
        background: var(--bg-surface-alt);
        color: var(--color-text-main);
      }
    `,
  ],
  template: `
    @if (shown().length) {
      <span class="often-label">Used in</span>
      @for (writing of shown(); track writing.id) {
        <button appChip type="button" class="often-chip used-in-chip" [title]="'Open ' + writing.name + ' in the Studio'"
          (click)="open(writing)">{{ writing.name }}</button>
      }
      @if (rest().length) {
        <button appChip type="button" class="often-chip used-in-more" [nostosMenuTriggerFor]="moreWritings"
          [attr.aria-label]="rest().length === 1 ? '1 more writing' : rest().length + ' more writings'">+{{ rest().length }}</button>
        <ng-template #moreWritings>
          <nostos-menu aria-label="More writings that use this topic">
            @for (writing of rest(); track writing.id) {
              <button nostosMenuItem icon="file-text" (triggered)="open(writing)">{{ writing.name }}</button>
            }
          </nostos-menu>
        </ng-template>
      }
    }
  `,
})
export class BrainTopicUsedInComponent {
  private readonly router = inject(Router);

  readonly usage = input<TopicWritingUsageDto | null>(null);

  private readonly writings = computed(() => this.usage()?.writings ?? []);
  /** Newest edited first, as the server orders them. */
  readonly shown = computed(() => this.writings().slice(0, USED_IN_SHOWN));
  readonly rest = computed(() => this.writings().slice(USED_IN_SHOWN));

  open(writing: TopicWritingDto): void {
    void this.router.navigate(['/studio'], { queryParams: { writingId: writing.id } });
  }
}
