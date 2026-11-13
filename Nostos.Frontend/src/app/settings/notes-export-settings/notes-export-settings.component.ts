import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';

import { PortableLibraryService } from '../../core/services/portable-library.service';
import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { CheckboxDirective } from '../../ui/check/check.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { fileNameFromDisposition, saveDownload as saveBlobDownload } from '../settings-download';

/**
 * Settings → Backups & data → Take your notes elsewhere (issue #1221).
 *
 * Notes for reading in other tools: a zip of plain Markdown, one file per book
 * and one per topic, with `[[Topic]]` links kept as links so the folder opens as
 * an Obsidian vault. Not a backup and not a transfer; it is never imported back.
 */
@Component({
  selector: 'app-notes-export-settings',
  standalone: true,
  imports: [ButtonComponent, CheckboxDirective, NostosIconComponent],
  templateUrl: './notes-export-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './notes-export-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotesExportSettingsComponent {
  private readonly portableLibrary = inject(PortableLibraryService);
  private readonly toast = inject(ToastService);

  readonly notesExportBusy = signal(false);
  readonly notesExportError = signal<string | null>(null);
  readonly notesExportWritings = signal(true);

  exportNotesAsMarkdown(): void {
    if (this.notesExportBusy()) return;

    this.notesExportBusy.set(true);
    this.notesExportError.set(null);
    this.portableLibrary.exportNotesMarkdown(this.notesExportWritings()).subscribe({
      next: (response) => {
        this.notesExportBusy.set(false);
        if (!response.body || response.body.size === 0) {
          this.notesExportError.set('Nostos returned an empty export. Try again.');
          return;
        }
        this.saveNotesArchive(
          response.body,
          response.headers.get('content-disposition'),
        );
        this.toast.success('Your notes are ready.');
      },
      error: (error) => {
        this.notesExportBusy.set(false);
        this.notesExportError.set(
          error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403)
            ? 'Your session no longer allows this export. Sign in again, then retry.'
            : 'Nostos could not export your notes. Nothing was changed. Try again.',
        );
      },
    });
  }

  private saveNotesArchive(blob: Blob | null, contentDisposition: string | null): void {
    if (!blob) return;
    this.saveDownload(blob, fileNameFromDisposition(contentDisposition, 'nostos-notes.zip', '.zip'));
  }

  /** The one place the page hands bytes to the browser; a seam the spec replaces. */
  private saveDownload(blob: Blob, fileName: string): void {
    saveBlobDownload(blob, fileName);
  }
}
