export type DeploymentMode = 'SelfHosted' | 'Cloud';

export interface DeploymentCapabilities {
  deploymentMode: DeploymentMode;
  requiresAuthentication: boolean;
  canConfigureAiProvider: boolean;
  managedAi: boolean;
  managedVoiceTranscription: boolean;
  usesCloudStorage: boolean;
  supportsLocalBackupConfiguration: boolean;
  supportsPrivateNetworkAccess: boolean;
  supportsEreaderAccess: boolean;
  usageMeteringAvailable: boolean;
  /**
   * Server-authoritative migration capability for #680. Optional until the
   * backend advertises it: hosts without the field fail closed (no transfer
   * controls), never inferred from deployment mode (plan §4).
   */
  supportsLibraryMigration?: boolean;
  /** True only once safe replacement/activation (#681) is available. */
  supportsSafeActivation?: boolean;
  /**
   * Server-authoritative direct part-upload capability for library migration
   * (#680 slice B9). When true the host may answer a part-upload request with
   * a short-lived storage target and the browser sends the archive part
   * straight there instead of through the application server. The target is
   * described only by URL, method, required headers and expiry; no storage
   * provider details cross this boundary. Absent/false keeps the current
   * application-server chunk path, byte for byte (plan §42, §78).
   */
  supportsDirectPartUpload?: boolean;
  accountManagementUrl?: string | null;
  feedbackUrl?: string | null;
  hostedBrowserIntegrationEnabled?: boolean;
  /** Hosted nightly backup history; absent/false keeps the customer card hidden. */
  supportsManagedBackups?: boolean;
}
