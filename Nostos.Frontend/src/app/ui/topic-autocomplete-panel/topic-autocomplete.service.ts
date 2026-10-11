import { Injectable, computed, signal } from '@angular/core';
import { TopicDto } from '../../core/services/topics.service';

/**
 * How the picker presents itself.
 *
 * `dialog` is the deliberate path — the "Link a topic" button — and gets a real
 * dialog with its own search field. `inline` is the typing path (`[[`): the
 * textarea is already the search field, so it only needs a suggestion list.
 */
export type TopicPickerMode = 'dialog' | 'inline';

let nextPickerId = 0;

@Injectable({ providedIn: 'root' })
export class TopicAutocompleteService {
  /** Unique per instance, so several note inputs on one page keep distinct ids. */
  readonly uid = `topic-picker-${nextPickerId++}`;
  readonly listId = `${this.uid}-list`;

  suggestions = signal<TopicDto[]>([]);
  activeIndex = signal(0);
  pickerOpen = signal(false);
  mode = signal<TopicPickerMode>('dialog');
  query = signal('');

  /** Bumped on every arrow-key move, so the list can keep the active row in view. */
  readonly keyboardMoves = signal(0);

  private readonly topics = signal<TopicDto[]>([]);

  /**
   * The name a "Create" row would link, or null when the query is empty or
   * already names an existing topic. Checked against every topic, not just the
   * visible slice, so a match beyond the first 50 is never offered as new.
   */
  readonly createName = computed(() => {
    const name = this.query().trim();
    if (!name) return null;
    const normalized = name.toLocaleLowerCase();
    const exists = this.topics().some(
      (topic) => topic.name.trim().toLocaleLowerCase() === normalized
    );
    return exists ? null : name;
  });

  /** Suggestions plus the create row: everything the arrow keys can reach. */
  readonly optionCount = computed(
    () => this.suggestions().length + (this.createName() ? 1 : 0)
  );

  readonly activeOptionId = computed(() =>
    this.optionCount() > 0 ? this.optionId(this.activeIndex()) : null
  );

  optionId(index: number): string {
    return `${this.uid}-option-${index}`;
  }

  setTopics(list: TopicDto[]): void {
    this.topics.set(list);
    if (this.pickerOpen()) this.setQuery(this.query());
  }

  openPicker(query = '', mode: TopicPickerMode = 'dialog'): void {
    this.mode.set(mode);
    this.pickerOpen.set(true);
    this.setQuery(query);
  }

  setQuery(query: string): void {
    this.query.set(query);
    const normalized = query.trim().toLocaleLowerCase();
    const matches = normalized
      ? this.topics().filter((topic) =>
          topic.name.toLocaleLowerCase().includes(normalized)
        )
      : this.topics();

    // An exact match leads, so it can never fall outside the visible slice.
    const exact = matches.findIndex(
      (topic) => topic.name.trim().toLocaleLowerCase() === normalized
    );
    const ordered =
      exact > 0
        ? [matches[exact], ...matches.slice(0, exact), ...matches.slice(exact + 1)]
        : matches;

    this.suggestions.set(ordered.slice(0, 50));
    this.activeIndex.set(0);
  }

  update(text: string, cursorPos: number): void {
    const start = text.lastIndexOf('[[', cursorPos);
    if (start === -1) {
      this.clear();
      return;
    }

    // Only the most recent unmatched opening brackets own the picker. Once a
    // closing pair occurs after them, the link is complete and the popup closes.
    const lastClose = text.lastIndexOf(']]', Math.max(0, cursorPos - 1));
    if (lastClose > start) {
      this.clear();
      return;
    }

    const fragment = text.substring(start + 2, cursorPos);
    if (fragment.includes('[') || fragment.includes(']') || fragment.includes('\n')) {
      this.clear();
      return;
    }

    this.openPicker(fragment, 'inline');
  }

  moveUp(): void {
    const count = this.optionCount();
    if (count === 0) return;
    this.activeIndex.update((current) => (current - 1 + count) % count);
    this.keyboardMoves.update((moves) => moves + 1);
  }

  moveDown(): void {
    const count = this.optionCount();
    if (count === 0) return;
    this.activeIndex.update((current) => (current + 1) % count);
    this.keyboardMoves.update((moves) => moves + 1);
  }

  /** The topic name the active row would link: an existing topic or the new one. */
  activeName(): string | null {
    const topic = this.suggestions()[this.activeIndex()];
    if (topic) return topic.name;
    return this.activeIndex() === this.suggestions().length ? this.createName() : null;
  }

  clear(): void {
    this.pickerOpen.set(false);
    this.query.set('');
    this.suggestions.set([]);
    this.activeIndex.set(0);
  }
}
