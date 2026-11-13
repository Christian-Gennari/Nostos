import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, EMPTY, exhaustMap, interval, takeUntil } from 'rxjs';

import {
  BackupHistoryItem,
  BackupProgress,
  BackupSettings,
  BackupStatus,
  backupStatusLabel,
  backupStatusTone,
  backupWarning,
} from '../../core/dtos/backup.dtos';
import { BackupService } from '../../core/services/backup.service';
import { formatNostosDate } from '../../core/date-formats';
import { formatFileSize, plural } from '../../core/format';
import { ToastService } from '../../core/services/toast.service';
import { BadgeComponent } from '../../ui/badge/badge.component';
import { ButtonComponent } from '../../ui/button/button.component';
import { ConfirmModal } from '../../ui/confirm-modal/confirm-modal.component';
import { DropdownComponent, type DropdownOption } from '../../ui/dropdown/dropdown.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { NostosDatePipe } from '../../ui/pipes/nostos-date.pipe';
import { NoticeComponent } from '../../ui/notice/notice.component';
import { ProgressComponent } from '../../ui/progress/progress.component';
import { SwitchComponent } from '../../ui/switch/switch.component';

const SLOW_STEP_THRESHOLD_MS = 30_000;

/** How often the running backup or restore is asked where it has got to. */
const PROGRESS_POLL_MS = 1_000;

const defaultProgress: BackupProgress = {
  isRunning: false,
  currentStep: null,
  percentComplete: 0,
  startedAt: null,
  stepNumber: 0,
  totalSteps: 0,
};

/**
 * Settings → Backups & data → Protect this installation: the local operational
 * backup: its settings, its history, and the restore/delete questions.
 *
 * This is not a library archive; it recovers *this* installation (see the
 * purpose line when migration is advertised).
 *
 * Progress is polled, and the poll is a stream rather than a bare interval:
 * - one request at a time (`exhaustMap`), so a slow answer cannot be overtaken
 *   by a newer one and applied out of order;
 * - torn down by `takeUntilDestroyed`, so navigating away stops it;
 * - paused while the section is hidden and resumed when it is shown again,
 *   because a backup running behind another settings section needs no polling.
 */
@Component({
  selector: 'app-local-backup-settings',
  standalone: true,
  imports: [
    BadgeComponent,
    ButtonComponent,
    ConfirmModal,
    DropdownComponent,
    EmptyStateComponent,
    IconButtonComponent,
    NostosDatePipe,
    NostosIconComponent,
    NoticeComponent,
    ProgressComponent,
    SwitchComponent,
  ],
  templateUrl: './local-backup-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './local-backup-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LocalBackupSettingsComponent implements OnInit {
  private readonly backupService = inject(BackupService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /** False while another settings section is the open one: polling pauses. */
  readonly active = input(true);

  /** The deployment can move a library between installations, so the card says what a backup is not. */
  readonly supportsMigration = input(false);

  readonly backupIntervalOptions = [
    { value: '6', label: 'Every 6 hours' },
    { value: '12', label: 'Every 12 hours' },
    { value: '24', label: 'Daily' },
    { value: '168', label: 'Weekly' },
  ] satisfies readonly DropdownOption[];

  readonly maxBackupOptions = [
    { value: '3', label: '3' },
    { value: '5', label: '5' },
    { value: '10', label: '10' },
    { value: '20', label: '20' },
  ] satisfies readonly DropdownOption[];

  status = signal<BackupStatus>({
    isEnabled: false,
    provider: 'Local',
    lastBackupAt: null,
    lastBackupStatus: null,
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  });

  settings = signal<BackupSettings>({
    isEnabled: false,
    provider: 'Local',
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  });

  history = signal<BackupHistoryItem[]>([]);
  /** The history list stays collapsed until asked for; it grows with every backup. */
  historyExpanded = signal(false);
  backingUp = signal(false);
  restoring = signal(false);
  importing = signal(false);

  /** Backup id awaiting restore confirmation (asked through ConfirmModal). */
  pendingRestore = signal<string | null>(null);

  /** Backup id awaiting archive-delete confirmation (asked through ConfirmModal). */
  pendingBackupDelete = signal<string | null>(null);
  deletingBackup = signal(false);
  progress = signal<BackupProgress>(defaultProgress);
  readonly backupStatusLabel = backupStatusLabel;
  readonly backupStatusTone = backupStatusTone;

  showSlowNotice = signal(false);

  /** Newest completed backup: the status line, else the history. */
  private readonly lastCompletedBackupAt = computed(() => {
    const s = this.status();
    if (s.lastBackupAt && s.lastBackupStatus === 'Completed') return s.lastBackupAt;
    return (
      this.history()
        .filter((h) => h.status === 'Completed')
        .map((h) => h.createdAt)
        .sort()
        .at(-1) ?? null
    );
  });
  readonly backupWarning = computed(() =>
    backupWarning(
      this.settings().isEnabled,
      this.settings().intervalHours,
      this.lastCompletedBackupAt(),
    ),
  );

  private stepChangedAt = 0;

  /** Emits when the current poll should stop; a Subject, so it can be reused. */
  private readonly stopPolling$ = new Subject<void>();
  private polling = false;

  constructor() {
    effect(() => {
      const active = this.active();
      const busy = this.backingUp() || this.restoring();
      if (!active && this.polling) untracked(() => this.stopProgressPolling());
      else if (active && busy && !this.polling) untracked(() => this.startProgressPolling());
    });
  }

  ngOnInit(): void {
    this.reload();
  }

  /** Refresh the card from the server: status, settings and history. */
  reload(): void {
    this.loadData();
  }

  formatSize = formatFileSize;

  formatInterval(hours: number): string {
    if (hours < 24) return `${hours} hours`;
    if (hours === 24) return 'day';
    if (hours === 168) return 'week';
    return `${hours} hours`;
  }

  /** The warning's "Turn on": the same call as the switch below it. */
  enableAutomaticBackup(): void {
    this.backupService.updateSettings({ isEnabled: true }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  loadData(): void {
    this.backupService.getStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.toast.error('Could not load the backup status'),
    });

    this.backupService.getSettings().subscribe({
      next: (s) => this.settings.set(s),
      error: () => {},
    });

    this.loadHistory();
  }

  loadHistory(): void {
    this.backupService.getHistory().subscribe({
      next: (h) => this.history.set(h),
      error: () => {},
    });
  }

  toggleEnabled(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.backupService.updateSettings({ isEnabled: checked }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  toggleBookFiles(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.backupService.updateSettings({ includeBookFiles: checked }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  changeInterval(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ intervalHours: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  changeMaxBackups(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ maxBackups: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  triggerBackup(): void {
    this.backingUp.set(true);
    this.startProgressPolling();
    this.backupService.triggerBackup().subscribe({
      next: (result) => {
        this.backingUp.set(false);
        this.stopProgressPolling();
        this.resetProgress();
        if (result.status === 'Completed') {
          this.toast.success(`Backup created (${this.formatSize(result.sizeBytes)})`);
        } else {
          this.toast.error('Could not create the backup. Check the logs for details.');
        }
        this.loadData();
      },
      error: () => {
        this.backingUp.set(false);
        this.stopProgressPolling();
        this.resetProgress();
        this.toast.error('Could not create the backup. Check the logs for details.');
      },
    });
  }

  /**
   * The archive comes down in this tab as a download, from a link the page
   * clicks itself: no popup for a blocker to stop, and no blank tab left behind.
   */
  downloadBackup(id: string): void {
    const link = document.createElement('a');
    link.href = this.backupService.getDownloadUrl(id);
    link.download = '';
    link.rel = 'noopener';
    link.hidden = true;
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  scanForBackups(): void {
    this.importing.set(true);
    this.backupService.importExisting().subscribe({
      next: (imported) => {
        this.importing.set(false);
        if (imported.length > 0) {
          this.toast.success(`Found ${plural(imported.length, 'backup')} on disk`);
        } else {
          this.toast.info('No new backups found on disk');
        }
        this.loadHistory();
      },
      error: () => {
        this.importing.set(false);
        this.toast.error('Could not scan for backups');
      },
    });
  }

  restoreBackup(id: string): void {
    this.pendingRestore.set(id);
  }

  cancelRestore(): void {
    if (this.restoring()) return;
    this.pendingRestore.set(null);
  }

  confirmRestore(): void {
    const id = this.pendingRestore();
    if (!id || this.restoring()) return;

    // The question stays open (busy) until the server answers.
    this.restoring.set(true);
    this.startProgressPolling();
    this.backupService.restore(id).subscribe({
      next: (result) => {
        this.restoring.set(false);
        this.pendingRestore.set(null);
        this.stopProgressPolling();
        this.resetProgress();
        if (result.success) {
          this.toast.success(result.message);
        } else {
          this.toast.error(result.message);
        }
        this.loadHistory();
      },
      error: () => {
        this.restoring.set(false);
        this.pendingRestore.set(null);
        this.stopProgressPolling();
        this.resetProgress();
        this.toast.error('Could not restore the backup. Check the logs for details.');
      },
    });
  }

  deleteBackup(id: string): void {
    this.pendingBackupDelete.set(id);
  }

  cancelBackupDelete(): void {
    if (this.deletingBackup()) return;
    this.pendingBackupDelete.set(null);
  }

  confirmBackupDelete(): void {
    const id = this.pendingBackupDelete();
    if (!id || this.deletingBackup()) return;

    this.deletingBackup.set(true);
    this.backupService.deleteBackup(id).subscribe({
      next: () => {
        this.deletingBackup.set(false);
        this.pendingBackupDelete.set(null);
        this.toast.success('Backup deleted');
        this.loadHistory();
      },
      error: () => {
        this.deletingBackup.set(false);
        this.toast.error('Could not delete the backup');
      },
    });
  }

  /** A backup is named by when it was made, e.g. “the backup from Oct 24, 2026, 9:41 AM”. */
  backupName(id: string | null): string {
    const item = this.history().find((held) => held.id === id);
    const when = formatNostosDate(item?.createdAt, 'datetime');
    return when ? `the backup from ${when}` : 'the backup';
  }

  private resetProgress(): void {
    this.progress.set(defaultProgress);
    this.showSlowNotice.set(false);
  }

  private startProgressPolling(): void {
    if (this.polling) return;
    this.polling = true;
    this.stepChangedAt = Date.now();

    interval(PROGRESS_POLL_MS)
      .pipe(
        takeUntil(this.stopPolling$),
        takeUntilDestroyed(this.destroyRef),
        exhaustMap(() =>
          this.backupService.getProgress().pipe(catchError(() => EMPTY)),
        ),
      )
      .subscribe((p) => {
        const prev = this.progress();
        if (p.currentStep !== prev.currentStep) {
          this.stepChangedAt = Date.now();
          this.showSlowNotice.set(false);
        }
        this.progress.set(p);
      });

    interval(PROGRESS_POLL_MS)
      .pipe(takeUntil(this.stopPolling$), takeUntilDestroyed(this.destroyRef))
      .subscribe(() =>
        this.showSlowNotice.set(Date.now() - this.stepChangedAt > SLOW_STEP_THRESHOLD_MS),
      );
  }

  private stopProgressPolling(): void {
    if (!this.polling) return;
    this.polling = false;
    this.stopPolling$.next();
  }

  private refreshStatus(): void {
    this.backupService.getStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => {},
    });
  }
}
