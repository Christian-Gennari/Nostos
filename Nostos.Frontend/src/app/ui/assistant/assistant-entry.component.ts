/**
 * One transcript entry: a speaker's words, its sources and citations, its
 * artwork and its failure block.
 *
 * A presentation component on purpose. It derives the source/citation view
 * from the entry it was given and emits what the shell must own — navigation,
 * retry focusing and the review card's scroll anchor. It never sends a turn.
 */
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';

import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { NostosDatePipe } from '../pipes/nostos-date.pipe';
import { AssistantAllowanceService } from './assistant-allowance.service';
import { AssistantChangesetCardComponent } from './changeset-card/assistant-changeset-card.component';
import { CapturedNoteTopicsComponent } from './captured-note-topics.component';
import {
  failureAction as failureActionFor,
  type AssistantFailureAction,
} from './assistant-failure';
import { AssistantMarkdownPipe } from './assistant-markdown.pipe';
import { AssistantService } from './assistant.service';
import type {
  AssistantEntry,
  AssistantEvidenceReferenceDto,
  AssistantSourceReferenceDto,
  AssistantTurnArtifact,
} from './assistant.service';
import {
  buildEntrySourceView,
  canOpenEvidence,
  canOpenSource,
  citationPreviews,
  citedEvidence,
  citesSources as entryCitesSources,
  evidenceLabel,
  formatCitationNumbers,
  sourceExcerpt,
  sourceLabel,
  type EntrySourceDisplay,
} from './assistant-sources';

@Component({
  selector: '[appAssistantEntry]',
  standalone: true,
  imports: [
    ButtonComponent,
    NostosIconComponent,
    NostosDatePipe,
    AssistantMarkdownPipe,
    AssistantChangesetCardComponent,
    CapturedNoteTopicsComponent,
  ],
  templateUrl: './assistant-entry.component.html',
  styleUrl: './assistant-entry.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantEntryComponent {
  private readonly assistantService = inject(AssistantService);
  private readonly allowance = inject(AssistantAllowanceService);
  private readonly router = inject(Router, { optional: true });

  readonly entry = input.required<AssistantEntry>();

  /** The shell navigates; this component only says what was chosen. */
  readonly evidenceSelected = output<AssistantEvidenceReferenceDto>();
  readonly sourceSelected = output<AssistantSourceReferenceDto>();
  readonly failureActionSelected = output<AssistantFailureAction>();
  /** This exact turn was replayed; the shell returns the view to its reply. */
  readonly retried = output<void>();
  readonly changesetDetailsToggled = output<{ changesetId: string; open: boolean }>();

  readonly sourceView = computed(() => buildEntrySourceView(this.entry()));
  readonly citesSources = computed(() => entryCitesSources(this.entry()));
  readonly previews = computed(() => citationPreviews(this.entry()));

  /**
   * Book source groups are collapsed by default. The key includes the visible
   * entry identity because the same book may legitimately occur in many turns.
   */
  readonly expandedSourceGroups = signal<Set<string>>(new Set());

  readonly failureAction = computed<AssistantFailureAction>(() => {
    const entry = this.entry();
    const retryable = this.assistantService.retryableTurnId() === entry.turnId;
    const bookId = this.assistantService.bookIdForTurn(entry.turnId);
    const action = failureActionFor(this.failureCode(), {
      retryable,
      canConfigureAiProvider: this.allowance.canConfigureAiProvider(),
      accountManagementUrl: this.allowance.accountManagementUrl(),
      bookId,
    });

    // A retry control is only real while this exact turn is still the stored
    // one. An older failure keeps its sentence and Details, and still leads
    // somewhere concrete instead of offering a button that cannot replay it.
    if (action.kind === 'retry' && !retryable) {
      return bookId
        ? { kind: 'book', label: 'Open the book', bookId }
        : { kind: 'settings', label: 'Open Settings' };
    }
    return action;
  });

  /** The Cloud renewal date, only for the allowance failure that needs it. */
  readonly allowanceRenewsAt = computed<string | null>(() =>
    this.failureCode() === 'ai_usage_limit_reached'
      ? this.allowance.usage()?.renewsAtUtc ?? null
      : null,
  );

  sourceGroupNumbers(display: EntrySourceDisplay): string {
    if (display.kind !== 'group') return '';
    return formatCitationNumbers(
      display.items.flatMap((item) => (item.kind === 'evidence' ? [item.number] : [])),
    );
  }

  sourceGroupId(entryId: string, bookId: string): string {
    return `assistant-source-group-${encodeURIComponent(entryId)}-${encodeURIComponent(bookId)}`;
  }

  isSourceGroupExpanded(entryId: string, bookId: string): boolean {
    return this.expandedSourceGroups().has(`${entryId}:${bookId}`);
  }

  toggleSourceGroup(entryId: string, bookId: string): void {
    const key = `${entryId}:${bookId}`;
    this.expandedSourceGroups.update((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  /** A click on a citation marker opens its source, as the sources row does. */
  onReplyClick(event: MouseEvent): void {
    const link = (event.target as Element | null)?.closest?.('a.assistant-cite-link');
    if (!link) return;
    event.preventDefault();
    const number = Number(link.textContent?.trim());
    const evidence = citedEvidence(this.entry())[number - 1];
    if (evidence) this.evidenceSelected.emit(evidence);
  }

  evidenceLabel(evidence: AssistantEvidenceReferenceDto): string {
    return evidenceLabel(evidence);
  }

  sourceExcerpt(excerpt: string | null | undefined): string {
    return sourceExcerpt(excerpt);
  }

  sourceLabel(source: AssistantSourceReferenceDto): string {
    return sourceLabel(source);
  }

  canOpenEvidence(evidence: AssistantEvidenceReferenceDto): boolean {
    return canOpenEvidence(evidence, !!this.router);
  }

  canOpenSource(source: AssistantSourceReferenceDto): boolean {
    return canOpenSource(source, !!this.router);
  }

  openEvidence(evidence: AssistantEvidenceReferenceDto): void {
    this.evidenceSelected.emit(evidence);
  }

  openSource(source: AssistantSourceReferenceDto): void {
    this.sourceSelected.emit(source);
  }

  showProposalArtifact(artifact: AssistantTurnArtifact): boolean {
    if (artifact.kind !== 'proposal') return false;
    return !(this.entry().suggestions ?? []).some((suggestion) =>
      suggestion.noteId === artifact.proposal.noteId
      && (suggestion.value ?? suggestion.label) ===
        (artifact.proposal.value ?? artifact.proposal.label));
  }

  artifactLabel(artifact: AssistantTurnArtifact): string {
    switch (artifact.kind) {
      case 'capture':
        return 'Saved note';
      case 'action':
        return `Changed · ${this.humanizeCapability(artifact.capability)}`;
      case 'failure':
        // Cancelled failures are not rendered: the turn's stop message owns that state.
        // A real failure is the error entry's one block (#1556), not a second
        // "Failed · …" label. An incomplete turn's reply already explains what
        // is left; the label only marks it, and never says "Failed" about
        // changes that were applied.
        return artifact.state === 'incomplete' ? 'Not finished' : '';
      case 'proposal':
        return `Proposed · ${artifact.proposal.label} — ${artifact.proposal.reason}`;
      case 'changeset':
        return '';
      case 'evidence':
        return this.evidenceLabel(artifact.evidence);
    }
  }

  /** The raw typed code, kept behind Details for support. */
  failureCode(): string {
    const entry = this.entry();
    const artifact = (entry.artifacts ?? []).find(
      (candidate): candidate is Extract<AssistantTurnArtifact, { kind: 'failure' }> =>
        candidate.kind === 'failure' && candidate.state !== 'cancelled',
    ) ?? null;
    return entry.meta ?? artifact?.code ?? 'assistant_turn_failed';
  }

  /** Replay this exact turn through the same TurnId (§1560). */
  onRetryFailure(): void {
    if (!this.assistantService.retryTurn(this.entry().turnId)) return;
    this.retried.emit();
  }

  onFailureAction(action: AssistantFailureAction): void {
    this.failureActionSelected.emit(action);
  }

  onChangesetDetailsToggled(changesetId: string, open: boolean): void {
    this.changesetDetailsToggled.emit({ changesetId, open });
  }

  private humanizeCapability(capability: string): string {
    const labels: Record<string, string> = {
      notes_link_existing_topic: 'note linked to topic',
      notes_rewrite_capture: 'note rewritten',
      library_update_book: 'book updated',
      library_move_book: 'book moved',
      library_create_collection: 'collection created',
      library_delete_collection: 'collection deleted',
    };
    return labels[capability] ?? capability.replaceAll('_', ' ');
  }
}
