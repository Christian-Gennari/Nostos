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
  accountManagementUrl?: string | null;
  feedbackUrl?: string | null;
}
