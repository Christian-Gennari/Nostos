/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import { ChangeDetectionStrategy, Component, ElementRef, input, output, viewChild } from '@angular/core';

import type { BookType } from '../core/dtos/book.dtos';
import { BookCoverComponent } from '../ui/book-cover/book-cover.component';
import { ButtonComponent } from '../ui/button/button.component';
import { DropZoneComponent } from '../ui/drop-zone/drop-zone.component';
import { IconTileComponent } from '../ui/icon-tile/icon-tile.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import type { AddBookIntentKind } from './book-form';

/**
 * The file half of the Add book sheet: how the book arrived (a chosen file or a
 * local upload), what is moving, and the drop zones that change it.
 *
 * It renders and reports; the shell owns the chosen files, the upload run and
 * the create call. Kept as its own component so the review/metadata form is not
 * also a file picker (#1639).
 */
@Component({
  selector: 'app-add-book-file-step',
  standalone: true,
  imports: [
    BookCoverComponent,
    ButtonComponent,
    DropZoneComponent,
    IconTileComponent,
    NostosIconComponent,
    NoticeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './add-book-file-step.component.html',
  styleUrl: './add-book-file-step.component.css',
})
export class AddBookFileStepComponent {
  readonly awaitingLocalFile = input(false);
  readonly isEditMode = input(false);
  readonly flowIntent = input.required<AddBookIntentKind>();
  readonly seededFromSource = input(false);
  readonly seededSourceName = input('the source');
  readonly selectedAssetLabel = input('');
  readonly sourceCoverUrl = input<string | null>(null);
  readonly formType = input.required<BookType>();
  readonly fileSummary = input<string | null>(null);
  readonly hasFile = input(false);
  /** The upload status line, or null when nothing is moving. */
  readonly uploadStatusText = input<string | null>(null);
  readonly uploadError = input<string | null>(null);
  readonly hasPendingUpload = input(false);
  readonly isSubmitting = input(false);
  /** The library's own book this add matched, when it already has a file. */
  readonly existingNotice = input<string | null>(null);
  readonly fileDragActive = input(false);

  readonly fileChosen = output<Event>();
  readonly bulkChosen = output<Event>();
  readonly dragOver = output<DragEvent>();
  readonly dragLeave = output<DragEvent>();
  readonly retryUpload = output<void>();
  readonly keepExisting = output<void>();
  readonly replaceExisting = output<void>();

  private readonly localFileInput = viewChild<ElementRef<HTMLInputElement>>('localFileInput');

  /** The shell focuses the first file choice when the upload door is chosen. */
  focusFileInput(): void {
    this.localFileInput()?.nativeElement?.focus();
  }
}
