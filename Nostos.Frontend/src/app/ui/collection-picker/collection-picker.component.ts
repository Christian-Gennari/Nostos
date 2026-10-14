import { Component, computed, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Collection } from '../../core/dtos/collection.dtos';
import { buildFlatTree } from '../flat-tree/flat-tree.helper';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { ButtonComponent } from '../button/button.component';

/**
 * Multi-select control for a book's collection membership.
 *
 * A book may belong to any number of collections, so a single-choice `<select>`
 * cannot express it: picking a second collection would silently replace the
 * first, and there was no way to remove one membership without removing them
 * all. This control is a checkbox list — the honest shape for a set — over the
 * same flattened hierarchy the sidebar uses, plus a chip row so current
 * memberships are readable without expanding anything.
 *
 * The parent owns the value (`selectedIds` in, `selectedIdsChange` out); this
 * component never mutates the array it is given.
 */
@Component({
  selector: 'app-collection-picker',
  standalone: true,
  imports: [CommonModule, NostosIconComponent, ButtonComponent],
  templateUrl: './collection-picker.component.html',
  styleUrl: './collection-picker.component.css',
})
export class CollectionPickerComponent {
  readonly collections = input.required<Collection[]>();
  readonly selectedIds = input<string[]>([]);
  readonly label = input('Collections');

  readonly selectedIdsChange = output<string[]>();
  /**
   * Every collection is treated as an expandable folder and all are expanded,
   * matching the add-book modal's previous behaviour (the full hierarchy must be
   * reachable in one view). Shared helper — not forked — so the picker, the
   * sidebar and the modal cannot drift on indentation semantics.
   */
  readonly options = computed(() => {
    const cols = this.collections();
    return buildFlatTree(cols, new Set(cols.map((c) => c.id)), true);
  });

  /** Selected ids that still resolve to a real collection (stale ids hidden). */
  readonly selected = computed(() => {
    const ids = new Set(this.selectedIds());
    return this.collections().filter((c) => ids.has(c.id));
  });

  readonly hasSelection = computed(() => this.selected().length > 0);

  isSelected(id: string): boolean {
    return this.selectedIds().includes(id);
  }

  toggle(id: string): void {
    const current = this.selectedIds();
    const next = current.includes(id)
      ? current.filter((x) => x !== id)
      : [...current, id];
    this.selectedIdsChange.emit(next);
  }

  remove(id: string): void {
    this.selectedIdsChange.emit(this.selectedIds().filter((x) => x !== id));
  }

  clear(): void {
    this.selectedIdsChange.emit([]);
  }
}
