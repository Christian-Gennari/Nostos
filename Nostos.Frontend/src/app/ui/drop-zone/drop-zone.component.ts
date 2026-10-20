/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';

export type DropZoneDensity = 'default' | 'compact';

/**
 * The dashed box a file is chosen in or dropped on.
 *
 * It is a `<label>` wrapping the real `<input type="file">`, so a click
 * anywhere opens the picker and the input, stretched invisibly over the box,
 * receives a drop natively. The component owns the box, its hover, focus and
 * drag-over states, the stretched input, and the two text tiers:
 *
 *   <label appDropZone [active]="dragActive()" (dragover)="..." (drop)="...">
 *     <input type="file" (change)="onFilesSelected($event)" />
 *     <nostos-icon name="upload-simple" [size]="20" weight="light" />
 *     <span class="nostos-drop-zone__title">Choose a file or drop it here</span>
 *     <span class="nostos-drop-zone__hint">EPUB, PDF</span>
 *   </label>
 *
 * What it does NOT own is the file handling: which types are accepted, what a
 * drop means and when `active` is true stay with the dialog, because each one
 * validates and uploads differently.
 *
 * WHY THIS EXISTS: Add Book drew this box three times and the highlight import
 * dialog copied its rules into a second stylesheet to match.
 */
@Component({
  selector: 'label[appDropZone]',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The file input and the text tiers are projected content, so the rules that
  // reach them cannot be scoped to this component's own (empty) template.
  encapsulation: ViewEncapsulation.None,
  template: '<ng-content />',
  host: {
    class: 'nostos-drop-zone',
    '[class.nostos-drop-zone--compact]': "density() === 'compact'",
    '[class.nostos-drop-zone--active]': 'active()',
  },
  styleUrl: './drop-zone.component.css',
})
export class DropZoneComponent {
  /** True while a file is being dragged over the box. */
  readonly active = input(false);
  readonly density = input<DropZoneDensity>('default');
}
