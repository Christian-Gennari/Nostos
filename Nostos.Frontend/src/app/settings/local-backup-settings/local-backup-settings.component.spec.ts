import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import {
  BackupHistoryItem,
  BackupProgress,
  BackupSettings,
  BackupStatus,
} from '../../core/dtos/backup.dtos';
import { BackupService } from '../../core/services/backup.service';
import { ToastService } from '../../core/services/toast.service';
import { LocalBackupSettingsComponent } from './local-backup-settings.component';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };

const runningProgress: BackupProgress = {
  isRunning: true,
  currentStep: 'Writing the archive',
  percentComplete: 40,
  startedAt: '2026-10-24T09:00:00Z',
  stepNumber: 2,
  totalSteps: 5,
};

const settingsFixture: BackupSettings = {
  isEnabled: true,
  provider: 'Local',
  includeBookFiles: true,
  intervalHours: 168,
  maxBackups: 3,
};

const statusFixture: BackupStatus = {
  isEnabled: true,
  provider: 'Local',
  lastBackupAt: '2026-10-20T09:00:00Z',
  lastBackupStatus: 'Completed',
  includeBookFiles: true,
  intervalHours: 168,
  maxBackups: 3,
};

const historyFixture: BackupHistoryItem[] = [
  {
    id: 'b1',
    createdAt: '2026-10-20T09:00:00Z',
    sizeBytes: 2_048,
    provider: 'Local',
    includeBookFiles: true,
    status: 'Completed',
  },
];

const backupServiceMock = {
  getStatus: vi.fn<() => Observable<BackupStatus>>(),
  getSettings: vi.fn<() => Observable<BackupSettings>>(),
  getHistory: vi.fn<() => Observable<BackupHistoryItem[]>>(),
  updateSettings: vi.fn<(settings: Partial<BackupSettings>) => Observable<BackupSettings>>(),
  triggerBackup: vi.fn<() => Observable<{ status: string; sizeBytes: number }>>(),
  getDownloadUrl: vi.fn(() => '/api/backups/b1/download'),
  importExisting: vi.fn(),
  restore: vi.fn<(id: string) => Observable<{ success: boolean; message: string }>>(),
  deleteBackup: vi.fn<(id: string) => Observable<unknown>>(),
  getProgress: vi.fn<() => Observable<BackupProgress>>(),
};

describe('LocalBackupSettingsComponent', () => {
  let fixture: ComponentFixture<LocalBackupSettingsComponent>;
  let component: LocalBackupSettingsComponent;

  async function configure(active = true, supportsMigration = false): Promise<void> {
    TestBed.resetTestingModule();
await TestBed.configureTestingModule({
      imports: [LocalBackupSettingsComponent],
      providers: [
        { provide: BackupService, useValue: backupServiceMock },
        { provide: ToastService, useValue: toastMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LocalBackupSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('active', active);
    fixture.componentRef.setInput('supportsMigration', supportsMigration);
    fixture.detectChanges();
  }

  beforeEach(() => {
    backupServiceMock.getStatus.mockClear();
    backupServiceMock.getStatus.mockReturnValue(of(statusFixture));
    backupServiceMock.getSettings.mockClear();
    backupServiceMock.getSettings.mockReturnValue(of(settingsFixture));
    backupServiceMock.getHistory.mockClear();
    backupServiceMock.getHistory.mockReturnValue(of(historyFixture));
    backupServiceMock.updateSettings.mockClear();
    backupServiceMock.updateSettings.mockImplementation((patch) =>
      of({ ...settingsFixture, ...patch }),
    );
    backupServiceMock.triggerBackup.mockClear();
    backupServiceMock.triggerBackup.mockReturnValue(of({ status: 'Completed', sizeBytes: 10 }));
    backupServiceMock.restore.mockClear();
    backupServiceMock.restore.mockReturnValue(of({ success: true, message: 'Restored.' }));
    backupServiceMock.deleteBackup.mockClear();
    backupServiceMock.deleteBackup.mockReturnValue(of(null));
    backupServiceMock.getProgress.mockClear();
    backupServiceMock.getProgress.mockReturnValue(of(runningProgress));
    toastMock.error.mockClear();
    toastMock.success.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('loads status, settings and history when the section mounts', async () => {
    await configure();

    expect(backupServiceMock.getStatus).toHaveBeenCalledTimes(1);
    expect(backupServiceMock.getSettings).toHaveBeenCalledTimes(1);
    expect(backupServiceMock.getHistory).toHaveBeenCalledTimes(1);
  });

  it('reloads from the server on demand, for a replaced library', async () => {
    await configure();
    backupServiceMock.getStatus.mockClear();
    backupServiceMock.getSettings.mockClear();
    backupServiceMock.getHistory.mockClear();

    component.reload();

    expect(backupServiceMock.getStatus).toHaveBeenCalledTimes(1);
    expect(backupServiceMock.getSettings).toHaveBeenCalledTimes(1);
    expect(backupServiceMock.getHistory).toHaveBeenCalledTimes(1);
  });

  it('polls progress once a second while a backup runs', async () => {
    const running = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(running.asObservable());
    await configure();
    vi.useFakeTimers();

    component.triggerBackup();

    expect(backupServiceMock.getProgress).not.toHaveBeenCalled();
    vi.advanceTimersByTime(3_000);

    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(3);
    expect(component.progress().percentComplete).toBe(40);
  });

  it('never has two progress requests in flight at once', async () => {
    const answers: Subject<BackupProgress>[] = [];
    backupServiceMock.getProgress.mockImplementation(() => {
      const answer = new Subject<BackupProgress>();
      answers.push(answer);
      return answer.asObservable();
    });
    const running = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(running.asObservable());
    await configure();
    vi.useFakeTimers();

    component.triggerBackup();
    vi.advanceTimersByTime(3_000);

    // Three ticks passed, but the first answer has not arrived: one request.
    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(1);

    answers[0].next(runningProgress);
    answers[0].complete();
    vi.advanceTimersByTime(1_000);

    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(2);
  });

  it('stops polling when the component is destroyed', async () => {
    const running = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(running.asObservable());
    await configure();
    vi.useFakeTimers();
    component.triggerBackup();
    vi.advanceTimersByTime(1_000);
    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(1);

    fixture.destroy();
    vi.advanceTimersByTime(5_000);

    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('pauses polling while the section is hidden and resumes when it is shown', async () => {
    await configure();
    vi.useFakeTimers();
    const pending = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(pending.asObservable());

    component.triggerBackup();
    vi.advanceTimersByTime(1_000);
    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(1);

    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    vi.advanceTimersByTime(5_000);

    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);

    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    vi.advanceTimersByTime(2_000);

    expect(backupServiceMock.getProgress).toHaveBeenCalledTimes(3);
    pending.complete();
  });

  it('stops polling once the backup answers', async () => {
    const pending = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(pending.asObservable());
    await configure();
    vi.useFakeTimers();

    component.triggerBackup();
    pending.next({ status: 'Completed', sizeBytes: 10 });
    pending.complete();
    vi.advanceTimersByTime(3_000);

    expect(backupServiceMock.getProgress).not.toHaveBeenCalled();
    expect(component.progress().isRunning).toBe(false);
  });

  it('warns about a slow step only after thirty seconds on the same one', async () => {
    const running = new Subject<{ status: string; sizeBytes: number }>();
    backupServiceMock.triggerBackup.mockReturnValue(running.asObservable());
    await configure();
    vi.useFakeTimers();

    component.triggerBackup();
    vi.advanceTimersByTime(10_000);
    expect(component.showSlowNotice()).toBe(false);

    // The first poll starts the clock for the step it reports, so the warning
    // follows one second after the thirtieth second on that step.
    vi.advanceTimersByTime(22_000);
    expect(component.showSlowNotice()).toBe(true);
    running.complete();
  });

  it('asks through the modal before restoring, and keeps it open while the server works', async () => {
    const pending = new Subject<{ success: boolean; message: string }>();
    backupServiceMock.restore.mockReturnValue(pending.asObservable());
    await configure();

    component.restoreBackup('b1');
    expect(component.pendingRestore()).toBe('b1');
    expect(backupServiceMock.restore).not.toHaveBeenCalled();

    component.confirmRestore();
    expect(backupServiceMock.restore).toHaveBeenCalledWith('b1');
    expect(component.restoring()).toBe(true);

    component.cancelRestore();
    expect(component.pendingRestore()).toBe('b1');

    pending.next({ success: true, message: 'Restored.' });
    pending.complete();
    expect(component.restoring()).toBe(false);
    expect(component.pendingRestore()).toBeNull();
  });

  it('says what a backup is when the deployment can also move a library', async () => {
    await configure(true, false);
    expect(fixture.nativeElement.querySelector('[data-testid="backup-purpose"]')).toBeNull();

    fixture.destroy();
    await configure(true, true);

    expect(fixture.nativeElement.querySelector('[data-testid="backup-purpose"]')).not.toBeNull();
  });
});
