import { Component, input, output } from '@angular/core';

import type { TopicDetailDto } from '../../core/services/topics.service';
import type { AssistantSuggestionDto } from '../../ui/assistant/assistant.service';
import { BadgeComponent } from '../../ui/badge/badge.component';
import { ButtonComponent } from '../../ui/button/button.component';

/** Where a request for suggested topics stands. `idle` shows nothing. */
export type BrainTopicProposalState =
  | 'idle'
  | 'loading'
  | 'ready'
  | 'empty'
  | 'unavailable'
  | 'error'
  | 'linking';

/**
 * The assistant's suggested topics for the note in focus.
 *
 * It shows what the Brain was told and reports what the reader chose. Asking
 * the assistant, deciding which suggestions are still valid and making a link
 * all stay with the Brain, so nothing is linked from here: accepting a
 * suggestion is a request, not a write.
 */
@Component({
  standalone: true,
  selector: 'app-brain-topic-proposals',
  imports: [BadgeComponent, ButtonComponent],
  templateUrl: './brain-topic-proposals.component.html',
  styleUrl: './brain-topic-proposals.component.css',
})
export class BrainTopicProposalsComponent {
  readonly state = input.required<BrainTopicProposalState>();
  readonly candidates = input.required<readonly AssistantSuggestionDto[]>();
  readonly message = input<string | null>(null);
  /** The existing topic whose evidence is being inspected, once loaded. */
  readonly detail = input<TopicDetailDto | null>(null);
  readonly inspectingId = input<string | null>(null);
  /** True when a link was accepted and failed, so it can be tried again. */
  readonly canRetryLink = input(false);

  readonly dismissed = output<void>();
  readonly linkRetried = output<void>();
  readonly suggestionsRetried = output<void>();
  readonly inspected = output<AssistantSuggestionDto>();
  readonly accepted = output<AssistantSuggestionDto>();
  readonly topicOpened = output<string>();
  readonly questionAsked = output<void>();
}
