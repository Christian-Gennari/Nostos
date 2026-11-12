import { Component, OnInit, computed, inject, input, signal, ChangeDetectionStrategy } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ButtonComponent } from '../ui/button/button.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { HighlightImportService } from '../core/services/highlight-import.service';
import { ReadwiseConnectionStatus, ReadwiseService } from '../core/services/readwise.service';
import { formatNostosDate } from '../core/date-formats';

/** Where Readwise keeps the access token the export API uses. */
export const READWISE_TOKEN_URL = 'https://readwise.io/access_token';

/**
 * Settings → Import → Readwise (issue #1525): connect the official export API
 * with an access token, beside the CSV export that remains the fallback.
 *
 * The token is sent once to the server, checked with Readwise's auth endpoint,
 * stored encrypted, and never rendered again. "Import now" opens the normal
 * highlight-import review; the server fetches the export and remembers the
 * updatedAfter watermark, so the next import only asks for changes.
 */
@Component({
  selector: 'app-readwise-import-settings',
  standalone: true,
  imports: [ButtonComponent, InputDirective],
  templateUrl: './readwise-import-settings.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './readwise-import-settings.component.css',
})
export class ReadwiseImportSettingsComponent implements OnInit {
  private readonly api = inject(ReadwiseService);
  private readonly highlightImport = inject(HighlightImportService);

  /**
   * Actions only, with no name or hint of its own: the import drop zone's
   * Readwise help line already names the source and says where the CSV is
   * (issue #1526). Settings and the component's own tests use the full row.
   */
  readonly inline = input(false);

  readonly tokenUrl = READWISE_TOKEN_URL;
  readonly status = signal<ReadwiseConnectionStatus | null>(null);
  readonly loading = signal(true);
  readonly formOpen = signal(false);
  readonly token = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  readonly connected = computed(() => this.status()?.connected === true);
  readonly lastImportLabel = computed(() => {
    const value = this.status()?.lastImportedAtUtc;
    return value ? `Last import ${formatNostosDate(value, 'date')}.` : '';
  });
  readonly throughLabel = computed(() => {
    const value = this.status()?.importedThroughUtc;
    return value ? `Next import fetches changes since ${formatNostosDate(value, 'date')}.` : '';
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.status().subscribe({
      next: (status) => {
        this.status.set(status);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        // The failure is not fatal: the token form stays reachable so the
        // owner can reconnect after a server restart or a key-ring change.
      },
    });
  }

  openForm(): void {
    if (this.busy()) return;
    this.token.set('');
    this.error.set(null);
    this.notice.set(null);
    this.formOpen.set(true);
  }

  cancelForm(): void {
    if (this.busy()) return;
    this.formOpen.set(false);
    this.token.set('');
    this.error.set(null);
  }

  setToken(value: string): void {
    this.token.set(value);
    this.error.set(null);
  }

  connect(): void {
    const value = this.token().trim();
    if (this.busy() || value.length === 0) return;

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.api.connect(value).subscribe({
      next: (status) => {
        this.status.set(status);
        this.formOpen.set(false);
        this.token.set('');
        this.notice.set('Readwise connected. Import now to bring in your highlights.');
        this.busy.set(false);
      },
      error: (error) => {
        this.error.set(this.failureMessage(error));
        this.busy.set(false);
      },
    });
  }

  disconnect(): void {
    if (this.busy()) return;

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.api.disconnect().subscribe({
      next: (status) => {
        this.status.set(status);
        this.notice.set('Readwise disconnected. The saved token was removed.');
        this.busy.set(false);
      },
      error: (error) => {
        this.error.set(this.failureMessage(error));
        this.busy.set(false);
      },
    });
  }

  /** Opens the review. The dialog asks the server for the export. */
  importNow(): void {
    this.error.set(null);
    this.notice.set(null);
    this.highlightImport.open('readwise');
  }

  /** The CSV export remains the fallback and is untouched. */
  importCsv(): void {
    this.highlightImport.open('all');
  }

  private failureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error?.error === 'string' && error.error.error.length > 0) {
        return error.error.error;
      }
      if (error.status === 0) return 'Nostos could not be reached. Check your connection and try again.';
      if (error.status === 401 || error.status === 403) return 'That token was not accepted by Readwise.';
    }
    return 'Readwise could not be reached. Check the token and try again.';
  }
}
