import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';

import { CloudEntryService } from '../../core/services/cloud-entry.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { PortableLibraryService } from '../../core/services/portable-library.service';
import { ToastService } from '../../core/services/toast.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { fileNameFromDisposition, saveDownload } from '../settings-download';

/**
 * Settings → Backups & data → Move to another Nostos on a host without the
 * shared transfer flow: download one library archive, or import one the browser
 * already has from a previous installation.
 *
 * The archive is a `.nostos` file the user can import into a fresh Nostos, not a
 * backup of this installation.
 */
@Component({
  selector: 'app-portable-export-settings',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent],
  templateUrl: './portable-export-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './portable-export-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortableExportSettingsComponent {
  private readonly portableLibrary = inject(PortableLibraryService);
  private readonly toast = inject(ToastService);
  private readonly capabilities = inject(DeploymentCapabilitiesService);
  readonly cloudEntry = inject(CloudEntryService);

  /** The dedicated `/settings/library` route. */
  readonly isManageLibraryPage = input(false);
  /** `?source=first-run`: this browser has just provisioned a Cloud library. */
  readonly isFirstRunImport = input(false);
  /** `?action=import`: the page was opened on the import choice. */
  readonly isImportSelected = input(false);

  readonly firstRunImportInput = viewChild<ElementRef<HTMLInputElement>>('firstRunImportInput');
  private readonly firstRunCloudImportElement =
    viewChild<ElementRef<HTMLElement>>('firstRunCloudImport');

  readonly portableExportBusy = signal(false);
  readonly portableExportProgress = signal<number | null>(null);
  readonly portableExportError = signal<string | null>(null);

  readonly showFirstRunCloudImport = computed(
    () =>
      this.isManageLibraryPage() &&
      (this.isFirstRunImport() || this.cloudEntry.firstRunImportPending()) &&
      this.capabilities.isCloud() &&
      !this.capabilities.supportsLibraryMigration(),
  );

  constructor() {
    // A link to `?action=import&source=first-run` should land on the picker, not
    // somewhere above it. Dropped once used, like `open=applebooks`.
    effect(() => {
      const section = this.firstRunCloudImportElement()?.nativeElement;
      if (!section || !this.isImportSelected() || !this.isFirstRunImport()) return;

      untracked(() => {
        section.scrollIntoView?.({ block: 'center' });
        section.focus({ preventScroll: true });
      });
    });
  }

  openFirstRunImportPicker(): void {
    this.firstRunImportInput()?.nativeElement.click();
  }

  async importFirstRunArchive(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    await this.cloudEntry.importPortableArchive(file);
    input.value = '';
  }

  exportAllNostosData(): void {
    if (this.portableExportBusy() || !this.capabilities.isCloud()) return;

    this.portableExportBusy.set(true);
    this.portableExportProgress.set(null);
    this.portableExportError.set(null);

    this.portableLibrary.exportArchive().subscribe({
      next: (event) => {
        if (event.type === HttpEventType.DownloadProgress) {
          this.portableExportProgress.set(
            event.total && event.total > 0
              ? Math.min(100, Math.round((event.loaded / event.total) * 100))
              : null,
          );
          return;
        }

        if (event instanceof HttpResponse) {
          this.savePortableArchive(event.body, event.headers.get('content-disposition'));
          if (this.portableExportError() === null) {
            this.portableExportBusy.set(false);
            this.portableExportProgress.set(null);
            this.toast.success('Your Nostos export is ready');
          }
        }
      },
      error: (error) => {
        this.portableExportBusy.set(false);
        this.portableExportProgress.set(null);
        this.portableExportError.set(this.portableExportFailureMessage(error));
        this.toast.error('Could not export your Nostos data');
      },
    });
  }

  private savePortableArchive(blob: Blob | null, contentDisposition: string | null): void {
    if (!blob) {
      this.portableExportBusy.set(false);
      this.portableExportProgress.set(null);
      this.portableExportError.set('Nostos returned an empty export. Try again.');
      this.toast.error('Could not export your Nostos data');
      return;
    }

    saveDownload(
      blob,
      fileNameFromDisposition(contentDisposition, 'nostos-export.nostos', '.nostos'),
    );
  }

  private portableExportFailureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401 || error.status === 403) {
        return 'Your session no longer allows this export. Sign in again, then retry.';
      }
      if (error.status === 409) {
        return 'This export is no longer available for the current account state.';
      }
    }

    return 'Nostos could not create the export. Your data was not changed. Try again.';
  }
}
