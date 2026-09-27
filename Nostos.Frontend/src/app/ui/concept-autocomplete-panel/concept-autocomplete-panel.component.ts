import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, inject, Output, ViewChild } from '@angular/core';
import { ConceptDto } from '../../core/services/concepts.service';
import { ConceptAutocompleteService } from './concept-autocomplete.service';

@Component({
  standalone: true,
  selector: 'concept-autocomplete-panel',
  imports: [CommonModule],
  template: `
    @if (auto.pickerOpen()) {
    <div class="autocomplete-panel" role="dialog" aria-label="Link a concept">
      <div class="picker-search">
        <input
          #searchInput
          type="search"
          [value]="auto.query()"
          placeholder="Find or create a concept..."
          aria-label="Find or create a concept"
          (input)="onSearchInput($event)"
          (keydown)="onSearchKeydown($event)"
        />
      </div>

      <div class="picker-list" role="listbox" aria-label="Concepts">
        @for (concept of auto.suggestions(); track concept.id; let i = $index) {
        <button
          type="button"
          class="item"
          role="option"
          [class.active]="i === auto.activeIndex()"
          [class.nostos-accent-rail]="i === auto.activeIndex()"
          [attr.aria-selected]="i === auto.activeIndex()"
          (mouseenter)="auto.activeIndex.set(i)"
          (click)="select(concept, $event)"
        >
          {{ concept.name }}
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
        <div class="picker-empty">Type a concept name.</div>
        }
      </div>

      <div class="picker-foot">
        <span>Links are saved as part of the note.</span>
        <button type="button" class="picker-cancel" (click)="cancel($event)">Cancel</button>
      </div>
    </div>
    }
  `,
  styleUrls: ['./concept-autocomplete-panel.css'],
})
export class ConceptAutocompletePanel {
  readonly auto = inject(ConceptAutocompleteService);

  @Output() conceptSelected = new EventEmitter<ConceptDto>();
  @Output() conceptNameSelected = new EventEmitter<string>();
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
      (concept) => concept.name.trim().toLocaleLowerCase() === query.toLocaleLowerCase()
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
      this.conceptSelected.emit(chosen);
      this.auto.clear();
      return;
    }

    if (this.canUseQuery()) {
      event.preventDefault();
      this.conceptNameSelected.emit(this.auto.query().trim());
      this.auto.clear();
    }
  }

  select(concept: ConceptDto, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.conceptSelected.emit(concept);
    this.auto.clear();
  }

  selectQuery(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    const query = this.auto.query().trim();
    if (!query) return;
    this.conceptNameSelected.emit(query);
    this.auto.clear();
  }

  cancel(event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    this.auto.clear();
    this.cancelled.emit();
  }
}
