import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { ButtonComponent } from '../../button/button.component';
import type {
  AssistantChangesetDto,
  LibraryChangesetCountsDto,
  LibraryChangesetDiffDto,
  LibraryCollectionChangeDto,
} from '../assistant.service';
import { CheckboxDirective } from '../../check/check.directive';
import { plural } from '../../../core/format';
import { kindCssVar } from '../../../brain/topic-map/topic-map.helpers';

/** How many books the details list shows before it summarises the rest. */
const BOOK_LIST_LIMIT = 40;

/** How many topic names a kind group shows before "and N more". */
const KIND_PREVIEW_LIMIT = 5;

/** One topic in a kind group, with its own tick state. */
interface KindGroupTopic {
  stepIndex: number | null;
  topicId: string | null;
  topicName: string;
  previousKindName: string | null;
  included: boolean;
  /** Why the server kept this step out of the review itself, when it did. */
  skippedReason: string | null;
}

/** A kind's topics as one review group with one checkbox. */
interface KindGroup {
  key: string;
  name: string;
  color: string | null;
  isNew: boolean;
  topics: KindGroupTopic[];
  /** The steps that give or clear one topic's kind, for the group checkbox. */
  stepIndexes: number[];
  /** The step that creates this kind, when the changeset does. */
  createStepIndex: number | null;
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

    if (this.inReview()) {
      // Nothing ticked and nothing in the diff are two different states: the
      // reader left everything out, or the library no longer needs any of it
      // (they set those kinds themselves before the apply).
      if (this.includedCount() === 0) {
        return this.skippedCount() > 0
          ? 'Nothing left to apply: these changes are already in the library.'
          : 'Every step is left out.';
      }
      if (this.diffEmpty()) return 'Nothing left to apply: these changes would do nothing.';
      return describe(diff.counts, diff.memberships.length, true);
    }

    if (this.diffEmpty()) return 'Nothing changed.';
    return describe(diff.counts, diff.memberships.length, false);
  });

  /**
   * True when the diff the server last computed changes nothing at all: every
   * collection, book, edit and topic-kind list is empty and every count is
   * zero. A review with an empty diff cannot be applied — an apply would run no
   * change, and the server refuses one that would leave the library as it is.
   */
  readonly diffEmpty = computed(() => {
    const diff = this.changeset().diff;
    const counts = diff.counts;
    const counted = counts.collectionsCreated + counts.collectionsDeleted + counts.collectionsUpdated
      + counts.membershipsAdded + counts.membershipsRemoved + counts.bookEdits
      + (counts.topicKindsCreated ?? 0) + (counts.topicKindChanges ?? 0);
    return counted === 0
      && diff.collections.length === 0
      && diff.memberships.length === 0
      && diff.bookEdits.length === 0
      && (diff.topicKinds?.length ?? 0) === 0
      && (diff.topicKindAssignments?.length ?? 0) === 0;
  });

  /** How many steps the server itself kept out because they would change nothing. */
  readonly skippedCount = computed(() =>
    this.changeset().steps.filter((step) => step.skippedReason).length);

  /**
   * The steps an apply would actually run. A kind's create step only counts
   * while at least one of its topics is ticked, exactly as the server's
   * included set drops it once every topic is left out.
   */
  readonly includedCount = computed(() => this.effectiveIncluded().length);
  readonly canApply = computed(() =>
    this.inReview() && !this.diffEmpty() && this.includedCount() > 0 && !this.disabled());

  /**
   * Groups are built from every stored step, not from the diff a preview
   * recomputes without the excluded ones, so unticking a topic (or a whole
   * kind) leaves it in place, unticked, instead of making it vanish into the
   * plain step list.
   */
  readonly kindGroups = computed<KindGroup[]>(() => {
    const steps = this.changeset().steps;
    const groups = new Map<string, KindGroup>();
    const createdOrder = new Map<string, number>();

    for (const step of steps) {
      const info = step.topicKind;
      if (!info?.createsKind || createdOrder.has(info.kindKey)) continue;
      createdOrder.set(info.kindKey, createdOrder.size);
    }

    for (const step of steps) {
      const info = step.topicKind;
      if (!info || info.createsKind) continue;
      let group = groups.get(info.kindKey);
      if (!group) {
        group = {
          key: info.kindKey,
          name: info.kindName,
          // Every kind has a palette colour, existing or new.
          color: info.colorKey ? kindCssVar(info.colorKey) : null,
          isNew: info.isNewKind,
          topics: [],
          stepIndexes: [],
          createStepIndex: steps.find((candidate) =>
            candidate.topicKind?.createsKind && candidate.topicKind.kindKey === info.kindKey)?.index ?? null,
          included: false,
        };
        groups.set(info.kindKey, group);
      }
      group.topics.push({
        stepIndex: step.index,
        topicId: info.topicId,
        topicName: info.topicName ?? step.summary,
        previousKindName: info.previousKindName,
        included: step.included,
        skippedReason: step.skippedReason ?? null,
      });
      group.stepIndexes.push(step.index);
    }

    const ordered = [...groups.values()];
    for (const group of ordered) {
      group.included = group.topics.some((topic) => topic.included);
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
    const grouped = new Set(this.kindGroups().map((group) => group.key));
    return this.changeset().steps.filter((step) => {
      const info = step.topicKind;
      if (!info) return true;
      // A kind's create step is represented by its group; a kind created on
      // its own has no group and keeps its own line.
      return info.createsKind && !grouped.has(info.kindKey);
    });
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
    this.detailKindGroups().length + this.collectionLines().length + this.bookLines().length + this.edits().length > 0);

  /**
   * The kind groups "Show every change" lists: while in review every stored
   * topic keeps its place (ticked or not), so a wrong guess can still be
   * unticked. Once the card is applied, undone or set aside, the rows come from
   * the diff itself — the same place the collection, book and edit lines come
   * from — so only what happened is listed. A topic the server kept out of the
   * apply (the reader set its kind themselves) never made it into the diff and
   * so is not listed as applied.
   */
  readonly detailKindGroups = computed<KindGroup[]>(() => {
    if (this.inReview()) return this.kindGroups();
    return appliedKindGroups(this.changeset().diff);
  });

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

  private effectiveIncluded(): number[] {
    const steps = this.changeset().steps;
    const included = new Set(steps.filter((step) => step.included).map((step) => step.index));

    // A kind's create step is only applied while one of the steps that needs
    // it is applied. Using the stored dependsOn edges (rather than guessing
    // from kind keys) keeps the client's canApply in step with the server's
    // Included(), even when a fallback plan carries a multi-topic kind op.
    const dependants = new Map<number, number[]>();
    for (const step of steps) {
      for (const dependency of step.dependsOn) {
        const list = dependants.get(dependency) ?? [];
        list.push(step.index);
        dependants.set(dependency, list);
      }
    }

    return steps
      .filter((step) => {
        if (!step.included) return false;
        if (!step.topicKind?.createsKind) return true;
        const users = dependants.get(step.index) ?? [];
        // A kind created on its own has no dependants and stays.
        return users.length === 0 || users.some((index) => included.has(index));
      })
      .map((step) => step.index);
  }

  toggleKindTopic(stepIndex: number | null, event: Event): void {
    if (stepIndex === null) return;
    this.toggleStep(stepIndex, event);
  }

  /** How many of a group's topics are still ticked. */
  groupIncludedCount(group: KindGroup): number {
    return group.topics.filter((topic) => topic.included).length;
  }

  /** "2" when the whole group is ticked, "1/2" while part of it is left out. */
  groupCountLabel(group: KindGroup): string {
    const included = this.groupIncludedCount(group);
    return included === group.topics.length ? `${group.topics.length}` : `${included}/${group.topics.length}`;
  }

  /** "Dewey, Freire, Montessori, and 11 more" under a group's checkbox. */
  groupPreview(group: KindGroup): string {
    // The preview is what Apply would do: an unticked name is not in it.
    const names = group.topics
      .filter((topic) => topic.included)
      .map((topic) => topic.topicName);
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
  parts.push(...kindChangeParts(counts, verb));
  if (counts.topicKindsCreated) {
    parts.push(`${verb('create', 'created')} ${plural(counts.topicKindsCreated, 'topic kind')}`);
  }

  const sentence = parts.length > 1
    ? `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
    : parts[0] ?? '';
  // A diff whose counts are all zero still gets a sentence, never "This will .":
  // the server and this card must not disagree about a change that does nothing.
  if (!sentence) return `${future ? 'This will ' : 'I '}${verb('change', 'changed')} nothing.`;
  return `${future ? 'This will ' : 'I '}${sentence}.`;
}

/**
 * The kind changes in their own words: giving a kind, changing one and removing
 * one are three different acts, and a clear is not a gift. A host that predates
 * the split falls back to the neutral "change the kind of N topics".
 */
function kindChangeParts(
  counts: LibraryChangesetCountsDto,
  verb: (present: string, past: string) => string,
): string[] {
  const given = counts.topicKindsGiven ?? 0;
  const changed = counts.topicKindsChanged ?? 0;
  const cleared = counts.topicKindsCleared ?? 0;
  if (given + changed + cleared === 0) {
    return counts.topicKindChanges
      ? [`${verb('change', 'changed')} the kind of ${plural(counts.topicKindChanges, 'topic')}`]
      : [];
  }

  const parts: string[] = [];
  if (given) parts.push(`${verb('give', 'gave')} ${plural(given, 'topic')} a kind`);
  if (changed) parts.push(`${verb('change', 'changed')} the kind of ${plural(changed, 'topic')}`);
  if (cleared) parts.push(`${verb('remove', 'removed')} the kind from ${plural(cleared, 'topic')}`);
  return parts;
}

/**
 * The kind groups a card that is no longer in review lists: straight from the
 * diff the server computed, the same source as its collection, book and edit
 * lines. A step the server kept out is not in the diff, so it cannot be listed
 * as applied.
 */
function appliedKindGroups(diff: LibraryChangesetDiffDto): KindGroup[] {
  const createdIds = new Set(
    (diff.topicKinds ?? [])
      .filter((kind) => kind.kind === 'created')
      .map((kind) => kind.id));
  const groups = new Map<string, KindGroup>();

  for (const assignment of diff.topicKindAssignments ?? []) {
    const key = assignment.kindId ?? 'none';
    let group = groups.get(key);
    if (!group) {
      group = {
        key,
        name: assignment.kindName ?? 'No kind',
        color: assignment.kindColorKey ? kindCssVar(assignment.kindColorKey) : null,
        isNew: assignment.kindId !== null && createdIds.has(assignment.kindId),
        topics: [],
        stepIndexes: [],
        createStepIndex: null,
        included: true,
      };
      groups.set(key, group);
    }
    group.topics.push({
      stepIndex: assignment.stepIndex ?? null,
      topicId: assignment.topicId,
      topicName: assignment.topicName,
      previousKindName: assignment.previousKindName,
      included: true,
      skippedReason: null,
    });
  }

  const ordered = [...groups.values()];
  ordered.sort((a, b) => {
    if (a.isNew !== b.isNew) return a.isNew ? -1 : 1;
    if (a.key === 'none') return 1;
    if (b.key === 'none') return -1;
    return a.name.localeCompare(b.name);
  });
  return ordered;
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
