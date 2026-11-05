export interface BackupStatus {
  isEnabled: boolean;
  provider: string;
  lastBackupAt: string | null;
  lastBackupStatus: string | null;
  includeBookFiles: boolean;
  intervalHours: number;
  maxBackups: number;
}

export interface BackupSettings {
  isEnabled: boolean;
  provider: string;
  includeBookFiles: boolean;
  intervalHours: number;
  maxBackups: number;
}

export interface UpdateBackupSettings {
  isEnabled?: boolean;
  provider?: string;
  includeBookFiles?: boolean;
  intervalHours?: number;
  maxBackups?: number;
}

export interface BackupHistoryItem {
  id: string;
  createdAt: string;
  sizeBytes: number;
  provider: string;
  status: string;
  includeBookFiles: boolean;
  errorMessage?: string;
}

export interface TriggerBackupResult {
  id: string;
  status: string;
  sizeBytes: number;
  createdAt: string;
}

export interface RestoreResult {
  success: boolean;
  message: string;
}

export interface BackupProgress {
  isRunning: boolean;
  currentStep: string | null;
  percentComplete: number;
  startedAt: string | null;
  stepNumber: number;
  totalSteps: number;
}
/** The words for a backup status, from the server enum name. */
export function backupStatusLabel(status: string | null | undefined): string {
  switch (status) {
    case 'InProgress':
      return 'In progress';
    default:
      return status ?? '';
  }
}

/** Completed is calm, Failed is the one alarm, anything else is neutral. */
export function backupStatusTone(status: string | null | undefined): 'success' | 'danger' | 'neutral' {
  return status === 'Completed' ? 'success' : status === 'Failed' ? 'danger' : 'neutral';
}

const DAY_MS = 24 * 60 * 60 * 1000;
/** With automatic backup off there is no schedule to measure against. */
const OFF_GRACE_DAYS = 14;

export interface BackupWarning {
  /** What the one action does: turn automatic backup on, or run a backup now. */
  action: 'enable' | 'backup';
  message: string;
}

/**
 * A quiet warning when this installation's backups cannot be trusted: automatic
 * backup is off and the last completed backup is more than 14 days old, or it
 * is on but the last completed backup is older than twice the interval.
 * Nothing is said before the first backup: that is a fresh install, not a lapse.
 */
export function backupWarning(
  isEnabled: boolean,
  intervalHours: number,
  lastCompletedAt: string | null | undefined,
  now: number = Date.now(),
): BackupWarning | null {
  if (!lastCompletedAt) return null;
  const last = Date.parse(lastCompletedAt);
  if (Number.isNaN(last)) return null;
  const ageMs = now - last;
  const days = Math.max(0, Math.floor(ageMs / DAY_MS));
  const ago = days === 1 ? '1 day ago' : `${days} days ago`;
  if (!isEnabled) {
    return ageMs > OFF_GRACE_DAYS * DAY_MS
      ? { action: 'enable', message: `Automatic backup is off. The last backup was ${ago}.` }
      : null;
  }
  return ageMs > 2 * intervalHours * 3600 * 1000
    ? { action: 'backup', message: `The last backup was ${ago}, older than your schedule allows.` }
    : null;
}
