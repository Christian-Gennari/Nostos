import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { ButtonComponent } from '../../button/button.component';
import type {
  AssistantChangesetDto,
  LibraryChangesetCountsDto,
  LibraryCollectionChangeDto,
  LibraryTopicKindAssignmentDto,
} from '../assistant.service';
import { CheckboxDirective } from '../../check/check.directive';
import { plural } from '../../../core/format';
import { kindCssVar } from '../../../brain/topic-map/topic-map.helpers';

/** How many books the details list shows before it summarises the rest. */
const BOOK_LIST_LIMIT = 40;

/** How many topic names a kind group shows before "and N more". */
const KIND_PREVIEW_LIMIT = 5;

/** A kind's topics as one review group with one checkbox. */
interface KindGroup {
  key: string;
  name: string;
  color: string | null;
  isNew: boolean;
  topics: LibraryTopicKindAssignmentDto[];
  stepIndexes: number[];
  included: boolean;
}

/**
 * One turn's library changes, as the user decides on them.
 *
 * A review says what would change and why it waits, lets the user leave steps
 * out, and applies or discards. An applied change says what changed and can be
 * undone. Every word here comes from the server's diff, never from the model,
 * so the card cannot claim something that did not happen.
 */
@Component({
  selector: 'app-assistant-changeset-card',
  standalone: true,
  imports: [CheckboxDirective, ButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './assistant-changeset-card.component.html',
  styleUrl: './assistant-changeset-card.component.css',
})
export class AssistantChangesetCardComponent {
  readonly changeset = input.required<AssistantChangesetDto>();
  /** What an undo left alone because it changed after the apply. */
  readonly conflicts = input<readonly string[]>([]);
  /** An action on this changeset is in flight. */
  readonly busy = input(false);
  /** Another changeset's action is in flight; this one waits. */
  readonly locked = input(false);
  readonly error = input<string | null>(null);
  /** True for the review a plain "yes" or "no" in the composer would answer. */
  readonly answerable = input(false);

  readonly apply = output<void>();
  readonly discard = output<void>();
  readonly undo = output<void>();
  readonly stepToggled = output<{ index: number; included: boolean }>();
  /** Several steps at once, for a kind group's one checkbox. */
  readonly stepsToggled = output<{ indexes: number[]; included: boolean }>();
  /** The full diff opened or closed; the transcript keeps this card's header in view. */
  readonly detailsToggled = output<boolean>();

  readonly detailsOpen = signal(false);

  readonly status = computed(() => this.changeset().status);
  readonly inReview = computed(() => this.status() === 'review');
  readonly disabled = computed(() => this.busy() || this.locked());

  readonly label = computed(() => {
    switch (this.status()) {
      case 'review':
        return 'Review before applying';
      case 'applied':
        return 'Changed';
      case 'undone':
        return 'Undone';
      default:
        return 'Set aside';
    }
  });

  readonly headline = computed(() => {
    const changeset = this.changeset();
    const diff = changeset.diff;
    // After an undo or a discard the counts would describe changes that no
    // longer stand, so the card says what became of them instead.
    if (this.status() === 'undone') return 'These changes were undone.';
    if (this.status() === 'discarded') return 'These changes were not applied. Nothing changed.';
    if (
      diff.collections.length + diff.memberships.length + diff.bookEdits.length
        + (diff.topicKinds?.length ?? 0) + (diff.topicKindAssignments?.length ?? 0) === 0
    ) {
      return this.inReview() ? 'Every step is left out.' : 'Nothing changed.';
    }
    return describe(changeset.diff.counts, diff.memberships.length, this.inReview());
  });

  readonly includedCount = computed(() => this.changeset().steps.filter((step) => step.included).length);
  readonly canApply = computed(() => this.inReview() && this.includedCount() > 0 && !this.disabled());

  readonly kindGroups = computed<KindGroup[]>(() => {
    const diff = this.changeset().diff;
    const assignments = diff.topicKindAssignments ?? [];
    if (assignments.length === 0) return [];

    const createdOrder = new Map<string, number>();
    const createdColors = new Map<string, string | null>();
    const createdSteps = new Map<string, number>();
    (diff.topicKinds ?? []).forEach((kind, order) => {
      if (kind.kind !== 'created') return;
      createdOrder.set(kind.id, order);
      createdColors.set(kind.id, kindCssVar(kind.colorKey));
      if (kind.stepIndex !== null && kind.stepIndex !== undefined) {
        createdSteps.set(kind.id, kind.stepIndex);
      }
    });

    const stepIncluded = new Map(this.changeset().steps.map((step) => [step.index, step.included]));
    const groups = new Map<string, KindGroup>();
    for (const assignment of assignments) {
      const key = assignment.kindId ?? 'none';
      let group = groups.get(key);
      if (!group) {
        group = {
          key,
          name: assignment.kindName ?? 'No kind',
          color: assignment.kindId ? createdColors.get(assignment.kindId) ?? null : null,
          isNew: assignment.kindId ? createdOrder.has(assignment.kindId) : false,
          topics: [],
          stepIndexes: [],
          included: true,
        };
        groups.set(key, group);
        if (assignment.kindId && createdSteps.has(assignment.kindId)) {
          group.stepIndexes.push(createdSteps.get(assignment.kindId)!);
        }
      }
      group.topics.push(assignment);
      if (assignment.stepIndex !== null) group.stepIndexes.push(assignment.stepIndex);
    }

    const ordered = [...groups.values()];
    for (const group of ordered) {
      group.included = group.stepIndexes.every((index) => stepIncluded.get(index) !== false);
    }
    ordered.sort((a, b) => {
      if (a.isNew !== b.isNew) return a.isNew ? -1 : 1;
      if (a.isNew && b.isNew) {
        return (createdOrder.get(a.key) ?? 0) - (createdOrder.get(b.key) ?? 0);
      }
      if (a.key === 'none') return 1;
      if (b.key === 'none') return -1;
      return a.name.localeCompare(b.name);
    });
    return ordered;
  });

  /** Steps that are not part of a kind group, still shown as their own lines. */
  readonly otherSteps = computed(() => {
    const kindSteps = new Set(this.kindGroups().flatMap((group) => group.stepIndexes));
    for (const kind of this.changeset().diff.topicKinds ?? []) {
      if (kind.stepIndex !== null && kind.stepIndex !== undefined) kindSteps.add(kind.stepIndex);
    }
    return this.changeset().steps.filter((step) => !kindSteps.has(step.index));
  });

  readonly collectionLines = computed(() => this.changeset().diff.collections.map(collectionLine));
  readonly bookLines = computed(() =>
    this.changeset().diff.memberships.slice(0, BOOK_LIST_LIMIT).map((change) => ({
      id: change.bookId,
      title: change.title,
      added: change.added,
      removed: change.removed,
    })),
  );
  readonly hiddenBooks = computed(() => Math.max(0, this.changeset().diff.memberships.length - BOOK_LIST_LIMIT));
  readonly edits = computed(() => this.changeset().diff.bookEdits);
  readonly hasDetails = computed(() =>
    this.kindGroups().length + this.collectionLines().length + this.bookLines().length + this.edits().length > 0);

  toggleDetails(): void {
    this.detailsOpen.update((open) => !open);
    this.detailsToggled.emit(this.detailsOpen());
  }

  toggleStep(index: number, event: Event): void {
    const included = (event.target as HTMLInputElement).checked;
    this.stepToggled.emit({ index, included });
  }

  /** One checkbox for a kind group ticks or unticks every topic in it. */
  toggleKindGroup(group: KindGroup, event: Event): void {
    const included = (event.target as HTMLInputElement).checked;
    this.stepsToggled.emit({ indexes: group.stepIndexes, included });
  }

  topicIncluded(stepIndex: number | null): boolean {
    if (stepIndex === null) return true;
    return this.changeset().steps[stepIndex]?.included !== false;
  }

  toggleKindTopic(stepIndex: number | null, event: Event): void {
    if (stepIndex === null) return;
    this.toggleStep(stepIndex, event);
  }

  /** "Dewey, Freire, Montessori, and 11 more" under a group's checkbox. */
  groupPreview(group: KindGroup): string {
    const names = group.topics.map((topic) => topic.topicName);
    if (names.length <= KIND_PREVIEW_LIMIT) return names.join(', ');
    return `${names.slice(0, KIND_PREVIEW_LIMIT).join(', ')}, and ${names.length - KIND_PREVIEW_LIMIT} more`;
  }

  /** A step that needs a left-out step cannot be ticked on its own. */
  stepBlocked(index: number): boolean {
    const changeset = this.changeset();
    const step = changeset.steps[index];
    return step.dependsOn.some((dependency) => !changeset.steps[dependency]?.included);
  }
}

/** One short line of what happens, in the right tense, from the server's totals. */
function describe(counts: LibraryChangesetCountsDto, booksRefiled: number, future: boolean): string {
  const parts: string[] = [];
  const verb = (present: string, past: string) => (future ? present : past);

  if (counts.collectionsDeleted) parts.push(`${verb('delete', 'deleted')} ${plural(counts.collectionsDeleted, 'collection')}`);
  if (counts.collectionsCreated) parts.push(`${verb('create', 'created')} ${plural(counts.collectionsCreated, 'collection')}`);
  if (counts.collectionsUpdated) parts.push(`${verb('rename or move', 'renamed or moved')} ${plural(counts.collectionsUpdated, 'collection')}`);
  if (booksRefiled) {
    const books = plural(booksRefiled, 'book');
    parts.push(counts.membershipsRemoved === 0
      ? `${verb('file', 'filed')} ${books}`
      : counts.membershipsAdded === 0
        ? `${verb('take', 'took')} ${books} out of collections`
        : `${verb('refile', 'refiled')} ${books}`);
  }
  if (counts.bookEdits) parts.push(`${verb('edit', 'edited')} ${plural(counts.bookEdits, 'book')}`);
  if (counts.topicKindChanges) {
    parts.push(`${verb('give', 'gave')} ${plural(counts.topicKindChanges, 'topic')} a kind`);
  }
  if (counts.topicKindsCreated) {
    parts.push(`${verb('create', 'created')} ${plural(counts.topicKindsCreated, 'topic kind')}`);
  }

  const sentence = parts.length > 1
    ? `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
    : parts[0] ?? '';
  return `${future ? 'This will ' : 'I '}${sentence}.`;
}

function collectionLine(change: LibraryCollectionChangeDto): { key: string; text: string; note: string | null } {
  const books = change.bookCount ? plural(change.bookCount, 'book') : null;
  switch (change.kind) {
    case 'deleted':
      return { key: `d:${change.id}`, text: `Delete “${change.name}”`, note: books ? `held ${books}` : 'empty' };
    case 'created':
      return {
        key: `c:${change.name}:${change.parentName ?? ''}`,
        text: change.parentName ? `New “${change.name}” in “${change.parentName}”` : `New “${change.name}”`,
        note: books,
      };
    default: {
      const renamed = change.previousName ? `Rename “${change.previousName}” to “${change.name}”` : null;
      const moved = change.parentChanged
        ? change.parentName ? `move into “${change.parentName}”` : 'move to the top level'
        : null;
      const text = renamed && moved ? `${renamed}, ${moved}`
        : renamed ?? `Move “${change.name}” ${moved?.replace(/^move /, '') ?? ''}`.trim();
      return { key: `u:${change.id}`, text, note: books };
    }
  }
}
