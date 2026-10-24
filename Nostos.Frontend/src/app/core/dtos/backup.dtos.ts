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
