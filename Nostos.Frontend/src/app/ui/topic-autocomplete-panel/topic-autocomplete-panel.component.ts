import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, inject, Output, ViewChild } from '@angular/core';
import { TopicDto } from '../../core/services/topics.service';
import { TopicAutocompleteService } from './topic-autocomplete.service';

@Component({
  standalone: true,
  selector: 'topic-autocomplete-panel',
  imports: [CommonModule],
  template: `
    @if (auto.pickerOpen()) {
    <div class="autocomplete-panel" role="dialog" aria-label="Link a topic">
      <div class="picker-search">
        <input
          #searchInput
          type="search"
          [value]="auto.query()"
          placeholder="Find or create a topic..."
          aria-label="Find or create a topic"
          (input)="onSearchInput($event)"
          (keydown)="onSearchKeydown($event)"
        />
      </div>

      <div class="picker-list" role="listbox" aria-label="Topics">
        @for (topic of auto.suggestions(); track topic.id; let i = $index) {
        <button
          type="button"
          class="item"
          role="option"
          [class.active]="i === auto.activeIndex()"
          [class.nostos-accent-rail]="i === auto.activeIndex()"
          [attr.aria-selected]="i === auto.activeIndex()"
          (mouseenter)="auto.activeIndex.set(i)"
          (click)="select(topic, $event)"
        >
          {{ topic.name }}
        </button>
        }

        @if (canUseQuery()) {
        <button
          type="button"
          class="item create-item"
          (click)="selectQuery($event)"
        >
          Use “{{ auto.query().trim() }}”
        </button>
        }

        @if (auto.suggestions().length === 0 && !canUseQuery()) {
        <div class="picker-empty">Type a topic name.</div>
        }
      </div>

      <div class="picker-foot">
        <span>Links are saved as part of the note.</span>
        <button type="button" class="picker-cancel" (click)="cancel($event)">Cancel</button>
      </div>
    </div>
    }
  `,
  styleUrls: ['./topic-autocomplete-panel.css'],
})
export class TopicAutocompletePanel {
  readonly auto = inject(TopicAutocompleteService);

  @Output() topicSelected = new EventEmitter<TopicDto>();
  @Output() topicNameSelected = new EventEmitter<string>();
  @Output() cancelled = new EventEmitter<void>();

  @ViewChild('searchInput') private searchInput?: ElementRef<HTMLInputElement>;

  focusSearch(): void {
    setTimeout(() => {
      const input = this.searchInput?.nativeElement;
      if (!input) return;
      input.focus();
      input.select();
    }, 0);
  }

  canUseQuery(): boolean {
    const query = this.auto.query().trim();
    if (!query) return false;
    return !this.auto.suggestions().some(
      (topic) => topic.name.trim().toLocaleLowerCase() === query.toLocaleLowerCase()
    );
  }

  onSearchInput(event: Event): void {
    this.auto.setQuery((event.target as HTMLInputElement).value);
  }

  onSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancel(event);
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.auto.moveDown();
      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.auto.moveUp();
      return;
    }

    if (event.key !== 'Enter') return;

    const chosen = this.auto.choose();
    if (chosen) {
      event.preventDefault();
      this.topicSelected.emit(chosen);
      this.auto.clear();
      return;
    }

    if (this.canUseQuery()) {
      event.preventDefault();
      this.topicNameSelected.emit(this.auto.query().trim());
      this.auto.clear();
    }
  }

  select(topic: TopicDto, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.topicSelected.emit(topic);
    this.auto.clear();
  }

  selectQuery(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    const query = this.auto.query().trim();
    if (!query) return;
    this.topicNameSelected.emit(query);
    this.auto.clear();
  }

  cancel(event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.auto.clear();
    this.cancelled.emit();
  }
}
