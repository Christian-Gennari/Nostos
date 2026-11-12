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
  /** Hosted restore adapter present; absent/false lists backups without a Restore action. */
  supportsManagedBackupRestore?: boolean;
  /** Hosted account deletion lifecycle; absent/false keeps the controls hidden. */
  supportsAccountDeletion?: boolean;
  /**
   * A SelfHosted server that can link an external media folder (#843),
   * whether or not one is set up yet. Absent/false keeps the Settings section
   * and the book page's linked-file states hidden; never inferred from
   * `deploymentMode`.
   */
  supportsExternalMediaLibraries?: boolean;
  /**
   * A SelfHosted server that can read a Calibre library folder (#1527),
   * whether or not one is set up yet. Absent/false keeps the folder path
   * hidden; never inferred from `deploymentMode`.
   */
  supportsCalibreImport?: boolean;
  /**
   * A server that accepts a zipped Calibre library through the resumable
   * upload (#1572). SelfHosted offers it beside the folder; Cloud offers it
   * instead of one. Absent/false keeps the upload path hidden.
   */
  supportsCalibreUpload?: boolean;
  /**
   * Resumable, chunked book-file uploads (#1266). When true the host maps the
   * part-upload contract and accepts a re-sent part after a dropped
   * connection. Absent/false keeps the legacy whole-request upload.
   */
  supportsResumableBookUploads?: boolean;
  /**
   * The server keeps a record of the AI apps connected over MCP (#1469):
   * SelfHosted access tokens, or Cloud's signed-in apps. Shown as Settings →
   * Connected apps; absent/false keeps the section hidden.
   */
  supportsMcpConnectedApps?: boolean;
  /**
   * The Stylos Sync Protocol (WP8) is served here: a writing device can pair
   * and sync, and Settings → Devices lists the paired ones. Absent/false keeps
   * the section hidden; never inferred from `deploymentMode`.
   */
  supportsStylosDevices?: boolean;
}
