/** The customer-facing account deletion lifecycle exposed by Nostos Cloud. */
export interface CloudAccountDeletionStatus {
  state: string;
  gracePeriodDays: number;
  requestedAtUtc: string | null;
  eligibleAtUtc: string | null;
  completedAtUtc: string | null;
  canCancel: boolean;
  portableExportUrl: string | null;
}

export interface CloudAccountDeletionRequest {
  confirm: boolean;
}
