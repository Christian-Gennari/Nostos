import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import type { TopicWritingUsageDto } from '../../core/services/topics.service';

/** "Used in" lists this many writings, newest first, then "and N more". */
export const USED_IN_SHOWN = 5;

/**
 * Where a topic is used in writing (issue #1162): the writings that cite one
 * of its notes, each opening in the Studio. Read-only: a writing reaches a
 * topic only through what it cites, so there is nothing to add or remove
 * here. Hidden when no writing uses the topic.
 */
@Component({
  selector: 'app-brain-topic-used-in',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './brain-topic-used-in.component.css',
  template: `
    @if (shown().length) {
      <section class="used-in" aria-labelledby="used-in-heading" data-testid="topic-used-in">
        <h2 class="used-in-heading" id="used-in-heading">Used in</h2>
        <ul class="used-in-list">
          @for (writing of shown(); track writing.id) {
            <li>
              <a class="used-in-link" routerLink="/studio" [queryParams]="{ writingId: writing.id }">{{ writing.name }}</a>
            </li>
          }
        </ul>
        @if (more() > 0) {
          <p class="used-in-more">and {{ more() }} more</p>
        }
      </section>
    }
  `,
})
export class BrainTopicUsedInComponent {
  readonly usage = input<TopicWritingUsageDto | null>(null);

  /** The newest first, as the server orders them, at most five. */
  readonly shown = computed(() => (this.usage()?.writings ?? []).slice(0, USED_IN_SHOWN));
  readonly more = computed(() => Math.max(0, (this.usage()?.total ?? 0) - this.shown().length));
}
