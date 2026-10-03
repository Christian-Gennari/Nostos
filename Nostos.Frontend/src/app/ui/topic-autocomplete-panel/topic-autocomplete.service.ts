import { Injectable, signal } from '@angular/core';
import { TopicDto } from '../../core/services/topics.service';

@Injectable({ providedIn: 'root' })
export class TopicAutocompleteService {
  suggestions = signal<TopicDto[]>([]);
  activeIndex = signal(0);
  pickerOpen = signal(false);
  query = signal('');

  private topics: TopicDto[] = [];

  setTopics(list: TopicDto[]): void {
    this.topics = list;
    if (this.pickerOpen()) this.setQuery(this.query());
  }

  openPicker(query = ''): void {
    this.pickerOpen.set(true);
    this.setQuery(query);
  }

  setQuery(query: string): void {
    this.query.set(query);
    const normalized = query.trim().toLocaleLowerCase();
    const matches = normalized
      ? this.topics.filter((topic) =>
          topic.name.toLocaleLowerCase().includes(normalized)
        )
      : this.topics;

    this.suggestions.set(matches.slice(0, 50));
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

    this.openPicker(fragment);
  }

  moveUp(): void {
    const list = this.suggestions();
    if (list.length === 0) return;
    this.activeIndex.update((current) => (current - 1 + list.length) % list.length);
  }

  moveDown(): void {
    const list = this.suggestions();
    if (list.length === 0) return;
    this.activeIndex.update((current) => (current + 1) % list.length);
  }

  choose(): TopicDto | null {
    const list = this.suggestions();
    if (list.length === 0) return null;
    return list[this.activeIndex()] ?? null;
  }

  clear(): void {
    this.pickerOpen.set(false);
    this.query.set('');
    this.suggestions.set([]);
    this.activeIndex.set(0);
  }
}
