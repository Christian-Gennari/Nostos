export interface ManagedBackupSummary {
  id: string;
  createdAtUtc: string;
  archiveBytes: number;
  mediaBytes: number;
  state: 'completed';
}

export interface ManagedBackupListing {
  retentionDays: number;
  backups: ManagedBackupSummary[];
}
