import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { ButtonComponent } from '../../button/button.component';
import type {
  AssistantChangesetDto,
  LibraryChangesetCountsDto,
  LibraryCollectionChangeDto,
} from '../assistant.service';
import { CheckboxDirective } from '../../check/check.directive';
import { plural } from '../../../core/format';

/** How many books the details list shows before it summarises the rest. */
const BOOK_LIST_LIMIT = 40;

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
    if (changeset.diff.collections.length + changeset.diff.memberships.length + changeset.diff.bookEdits.length === 0) {
      return this.inReview() ? 'Every step is left out.' : 'Nothing changed.';
    }
    return describe(changeset.diff.counts, changeset.diff.memberships.length, this.inReview());
  });

  readonly includedCount = computed(() => this.changeset().steps.filter((step) => step.included).length);
  readonly canApply = computed(() => this.inReview() && this.includedCount() > 0 && !this.disabled());

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
    this.collectionLines().length + this.bookLines().length + this.edits().length > 0);

  toggleDetails(): void {
    this.detailsOpen.update((open) => !open);
  }

  toggleStep(index: number, event: Event): void {
    const included = (event.target as HTMLInputElement).checked;
    this.stepToggled.emit({ index, included });
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
