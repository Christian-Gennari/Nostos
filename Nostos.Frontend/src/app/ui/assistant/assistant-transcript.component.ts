/**
 * The conversation transcript: every visible entry plus the live turn.
 *
 * It owns the per-entry children and forwards what the shell must handle
 * (navigation, the review card's scroll anchor, retry focus). It never changes
 * conversation state itself.
 */
import { ChangeDetectionStrategy, Component, ElementRef, inject, output } from '@angular/core';

import { AssistantService } from './assistant.service';
import type {
  AssistantEvidenceReferenceDto,
  AssistantSourceReferenceDto,
} from './assistant.service';
import { AssistantEntryComponent } from './assistant-entry.component';
import { ButtonComponent } from '../button/button.component';
import type { AssistantFailureAction } from './assistant-failure';

@Component({
  selector: '[appAssistantTranscript]',
  standalone: true,
  imports: [AssistantEntryComponent, ButtonComponent],
  templateUrl: './assistant-transcript.component.html',
  styleUrl: './assistant-transcript.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantTranscriptComponent {
  readonly assistant = inject(AssistantService);
  /** The host is the panel's scrollable content element. */
  readonly host = inject(ElementRef<HTMLElement>);

  readonly evidenceSelected = output<AssistantEvidenceReferenceDto>();
  readonly sourceSelected = output<AssistantSourceReferenceDto>();
  readonly failureActionSelected = output<AssistantFailureAction>();
  readonly retried = output<void>();
  readonly changesetDetailsToggled = output<{ changesetId: string; open: boolean }>();
}
