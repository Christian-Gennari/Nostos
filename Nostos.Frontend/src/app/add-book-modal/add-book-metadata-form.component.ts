/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

import type { Collection } from '../core/dtos/collection.dtos';
import { CollectionPickerComponent } from '../ui/collection-picker/collection-picker.component';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { DropZoneComponent } from '../ui/drop-zone/drop-zone.component';
import { FormFieldComponent } from '../ui/form-field/form-field.component';
import { InputDirective, TextareaDirective } from '../ui/form-control/form-control.directive';
import { ButtonComponent } from '../ui/button/button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { sanitizeDate, sanitizeIsbn, type AddBookIntentKind, type BookForm } from './book-form';

/**
 * The book record: essentials, the identifiers and the shallow "More details"
 * disclosure.
 *
 * The shell owns the draft object (`form`) because submit, the source import
 * and the file step all read it; this component edits that same object in
 * place, the way the inline form did before the split. `revision` exists only
 * for the cases where the shell changes the draft outside an event raised here
 * (an ISBN lookup, a source seed, a file read) — an OnPush view has to be told
 * to re-read a mutable object it was handed.
 */
@Component({
  selector: 'app-add-book-metadata-form',
  standalone: true,
  imports: [
    FormsModule,
    ButtonComponent,
    CollectionPickerComponent,
    DropdownComponent,
    DropZoneComponent,
    FormFieldComponent,
    InputDirective,
    NostosIconComponent,
    TextareaDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './add-book-metadata-form.component.html',
  styleUrl: './add-book-metadata-form.component.css',
})
export class AddBookMetadataFormComponent {
  readonly form = input.required<BookForm>();
  /** Bumped by the shell when it changed the draft outside this view. */
  readonly revision = input(0);
  readonly collections = input.required<Collection[]>();
  readonly bookTypeOptions = input.required<readonly DropdownOption[]>();
  readonly isEditMode = input(false);
  readonly flowIntent = input.required<AddBookIntentKind>();
  readonly isFetching = input(false);
  readonly seededFromSource = input(false);
  readonly coverPreview = input<string | null>(null);
  readonly selectedCoverName = input<string | null>(null);
  readonly coverDragActive = input(false);

  readonly submitted = output<void>();
  readonly typeChange = output<string>();
  readonly fetchMetadata = output<void>();
  readonly coverChosen = output<Event>();
  readonly coverDragOver = output<DragEvent>();
  readonly coverDragLeave = output<DragEvent>();

  private readonly titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');
  private readonly isbnInput = viewChild<ElementRef<HTMLInputElement>>('isbnInput');

  /** The shared draft, edited in place by the template's two-way bindings. */
  get model(): BookForm {
    return this.form();
  }

  /** The shell focuses these when a door is chosen (manual, paper, edit). */
  focusTitle(): void {
    this.titleInput()?.nativeElement?.focus();
  }

  focusIsbn(): void {
    this.isbnInput()?.nativeElement?.focus();
  }

  onTypeChange(type: string): void {
    if (type !== 'physical' && type !== 'ebook' && type !== 'audiobook') return;
    // The type is part of the shared draft; the shell also reacts to the event
    // because a file never belongs to a paper record.
    this.model.type = type;
    this.typeChange.emit(type);
  }

  sanitizeDate(value: string | null): string | null {
    return sanitizeDate(value);
  }

  sanitizeIsbn(value: string | null): string | null {
    return sanitizeIsbn(value);
  }
}
