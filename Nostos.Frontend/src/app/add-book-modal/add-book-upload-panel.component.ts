/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */

import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ButtonComponent } from '../ui/button/button.component';
import { IconTileComponent } from '../ui/icon-tile/icon-tile.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { ProgressComponent } from '../ui/progress/progress.component';

/**
 * The focused view while a book file moves (#1275).
 *
 * A section inside the existing sheet, not a second card: the mark, the file's
 * identity, one bar and the way out. Presentational on purpose — the run and
 * the stop question belong to `AddBookUploadSession` and the shell, so this
 * component only says what they report.
 */
@Component({
  selector: 'app-add-book-upload-panel',
  standalone: true,
  imports: [
    ButtonComponent,
    IconTileComponent,
    NostosIconComponent,
    NoticeComponent,
    ProgressComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './add-book-upload-panel.component.html',
  styleUrl: './add-book-upload-panel.component.css',
})
export class AddBookUploadPanelComponent {
  readonly bookTitle = input.required<string>();
  readonly fileName = input<string>('');
  readonly error = input<string | null>(null);
  readonly progress = input<number>(0);
  readonly statusText = input<string>('');
  readonly stopping = input(false);
  readonly submitting = input(false);

  readonly retry = output<void>();
  readonly stop = output<void>();
}
