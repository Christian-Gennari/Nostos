import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { of } from 'rxjs';

import { BrandLockupComponent } from '../ui/brand-lockup/brand-lockup.component';
import { ButtonComponent } from '../ui/button/button.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { CalibreImportService } from '../core/services/calibre-import.service';
import { CloudAccountDeletionStatus } from '../core/dtos/cloud-account-deletion.dtos';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { HighlightImportService, type HighlightImportStart } from '../core/services/highlight-import.service';

import { CarrelDevicesSettingsComponent } from './carrel-devices-settings.component';
import { ConnectedAppsSettingsComponent } from './connected-apps-settings.component';
import { ExternalMediaSettingsComponent } from './external-media-settings.component';
import { AiProviderSettingsComponent } from './ai-provider-settings/ai-provider-settings.component';
import { AssistantSettingsComponent } from './assistant-settings/assistant-settings.component';
import { BookSourcesSettingsComponent } from './book-sources-settings/book-sources-settings.component';
import { CloudAccountSettingsComponent } from './cloud-account-settings/cloud-account-settings.component';
import { EreaderAccessSettingsComponent } from './ereader-access-settings/ereader-access-settings.component';
import { ImportSettingsComponent } from './import-settings/import-settings.component';
import { LibraryMoveSettingsComponent } from './library-move-settings/library-move-settings.component';
import { LocalBackupSettingsComponent } from './local-backup-settings/local-backup-settings.component';
import { ManagedBackupsSettingsComponent } from './managed-backups-settings/managed-backups-settings.component';
import { NotesExportSettingsComponent } from './notes-export-settings/notes-export-settings.component';
import { PortableExportSettingsComponent } from './portable-export-settings/portable-export-settings.component';
import { ThemeSettingsComponent } from './theme-settings/theme-settings.component';
import {
  LINKABLE_SECTIONS,
  SECTION_ALIASES,
  SettingsSection,
} from './settings-sections';

/**
 * Settings: section navigation and capability gating.
 *
 * Every section is its own component (see `settings-sections.ts` for the
 * vocabulary they share). This page keeps what is genuinely page-level: which
 * section is open, the `?section=` mirroring, the capability gates that decide
 * which sections exist for this deployment, and the cross-section jumps. It
 * owns no section's form or HTTP state, so a change to one settings area stays
 * inside that area's files.
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    AiProviderSettingsComponent,
    AssistantSettingsComponent,
    BookSourcesSettingsComponent,
    BrandLockupComponent,
    ButtonComponent,
    CarrelDevicesSettingsComponent,
    CloudAccountSettingsComponent,
    ConnectedAppsSettingsComponent,
    EmptyStateComponent,
    EreaderAccessSettingsComponent,
    ExternalMediaSettingsComponent,
    ImportSettingsComponent,
    LibraryMoveSettingsComponent,
    LoadingIndicatorComponent,
    LocalBackupSettingsComponent,
    ManagedBackupsSettingsComponent,
    NotesExportSettingsComponent,
    NostosIconComponent,
    PortableExportSettingsComponent,
    ThemeSettingsComponent,
  ],
  templateUrl: './settings.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./settings-section.shared.css', './settings.component.css', './settings-section.responsive.css'],
})
export class SettingsComponent implements OnInit {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly router = inject(Router, { optional: true });
  private readonly queryParamMap = toSignal(
    this.route?.queryParamMap ?? of(this.route?.snapshot.queryParamMap ?? convertToParamMap({})),
    { initialValue: this.route?.snapshot.queryParamMap ?? convertToParamMap({}) },
  );
  private readonly deploymentCapabilitiesService = inject(DeploymentCapabilitiesService);
  private readonly highlightImport = inject(HighlightImportService);
  private readonly calibreImport = inject(CalibreImportService);
  readonly cloudEntry = inject(CloudEntryService);

  /** Refreshed when a library transfer replaced what the backup section reads. */
  private readonly localBackup = viewChild(LocalBackupSettingsComponent);

  /** `/settings/library`: the Backups & data section, kept as its own route. */
  readonly isManageLibraryPage = this.route?.snapshot.data['manageLibraryPage'] === true;
  /**
   * Which settings section is visible. Mirrored to `?section=` so a section
   * survives a reload and can be linked to.
   */
  readonly activeSettingsSection = signal<SettingsSection>(
    this.isManageLibraryPage ? 'data' : 'general',
  );
  readonly isImportSelected = computed(() => this.queryParamMap().get('action') === 'import');
  readonly isFirstRunImport = computed(() => this.queryParamMap().get('source') === 'first-run');

  /** Server-authoritative deployment capabilities. Null means not loaded yet. */
  readonly deploymentCapabilities = this.deploymentCapabilitiesService.capabilities;
  readonly capabilitiesFailed = this.deploymentCapabilitiesService.failed;
  readonly capabilitiesLoading = computed(
    () =>
      this.deploymentCapabilitiesService.loading() ||
      (this.deploymentCapabilities() === null && !this.capabilitiesFailed()),
  );
  readonly supportsLocalBackupConfiguration =
    this.deploymentCapabilitiesService.supportsLocalBackupConfiguration;
  readonly supportsManagedBackups = this.deploymentCapabilitiesService.supportsManagedBackups;
  readonly supportsPrivateNetworkAccess =
    this.deploymentCapabilitiesService.supportsPrivateNetworkAccess;
  readonly supportsEreaderAccess = this.deploymentCapabilitiesService.supportsEreaderAccess;
  readonly isCloud = this.deploymentCapabilitiesService.isCloud;
  readonly cloudAccountManagementUrl = computed(() =>
    this.isCloud() ? this.deploymentCapabilitiesService.accountManagementUrl() : null,
  );
  readonly supportsAccountDeletion = computed(
    () => this.isCloud() && this.deploymentCapabilitiesService.supportsAccountDeletion(),
  );
  readonly supportsCloudPortableExport = this.isCloud;
  /**
   * Server-authoritative migration capability (#680 plan §4). Only a true
   * value renders the shared "Move your library" card; false or absent keeps
   * this surface exactly as it is on main. Never inferred from
   * `deploymentMode`.
   */
  readonly supportsLibraryMigration = this.deploymentCapabilitiesService.supportsLibraryMigration;
  /**
   * Server-authoritative safe activation (#681). The shared import flow only
   * starts activation when this is true; false keeps the gated explanation.
   */
  readonly supportsSafeActivation = this.deploymentCapabilitiesService.supportsSafeActivation;
  /**
   * A SelfHosted server with an external media folder configured (#843).
   * Server-authoritative: never inferred from `deploymentMode`.
   */
  readonly supportsExternalMediaLibraries =
    this.deploymentCapabilitiesService.supportsExternalMediaLibraries;
  /** Either Calibre path is enough to open the import by link. */
  readonly supportsCalibre = this.deploymentCapabilitiesService.supportsCalibre;
  /**
   * Settings → Connected apps (#1469): a server that keeps a record of the AI
   * apps connected over MCP (SelfHosted access tokens; Cloud sign-ins once it
   * advertises them). Server-authoritative, never inferred from the mode.
   */
  readonly supportsMcpConnectedApps = this.deploymentCapabilitiesService.supportsMcpConnectedApps;
  /**
   * Settings → Devices (WP8): a server that serves the Carrel Sync Protocol.
   * Server-authoritative, never inferred from the deployment mode.
   */
  readonly supportsCarrelDevices = this.deploymentCapabilitiesService.supportsCarrelDevices;
  readonly hasManageLibraryActions = computed(
    () =>
      this.supportsLocalBackupConfiguration() ||
      this.supportsLibraryMigration() ||
      this.supportsCloudPortableExport(),
  );
  readonly manageLibrarySummary = computed(() => {
    const hasBackups = this.supportsLocalBackupConfiguration();
    const hasMigration = this.supportsLibraryMigration();

    if (hasBackups && hasMigration) {
      return 'Back up this installation or move your library to another Nostos.';
    }
    if (hasBackups) {
      return 'Backups help you recover this self-hosted installation.';
    }
    if (hasMigration) {
      return 'Move your library to another Nostos installation.';
    }
    if (this.supportsManagedBackups()) {
      return 'Nostos backs up your library every night. You can also download a library archive of it.';
    }
    if (this.supportsCloudPortableExport()) {
      return 'Download a library archive of your Nostos library.';
    }

    return 'Manage the data for your Nostos library.';
  });
  readonly managedEreaderAccess = computed(
    () => this.supportsEreaderAccess() && this.isCloud(),
  );
  readonly canConfigureAiProvider = this.deploymentCapabilitiesService.canConfigureAiProvider;
  readonly managedAi = this.deploymentCapabilitiesService.managedAi;
  readonly managedVoiceTranscription =
    this.deploymentCapabilitiesService.managedVoiceTranscription;
  readonly managedAiUsageAvailable = computed(
    () =>
      this.isCloud() &&
      this.managedAi() &&
      this.deploymentCapabilitiesService.usageMeteringAvailable(),
  );
  /** Backups & data is offered when this deployment has anything to put in it. */
  readonly hasDataSection = computed(
    () => this.isManageLibraryPage || this.hasManageLibraryActions(),
  );
  /** Whether the nav's second group ("This server" / "Your account") has entries. */
  readonly hasInfrastructureSections = computed(
    () =>
      this.canConfigureAiProvider() ||
      this.supportsExternalMediaLibraries() ||
      this.supportsMcpConnectedApps() ||
      this.supportsCarrelDevices() ||
      this.hasDataSection() ||
      this.isCloud(),
  );

  constructor() {
    effect(() => {
      const raw = this.queryParamMap().get('section');
      const requested = (raw && SECTION_ALIASES[raw]) || (raw as SettingsSection | null);
      if (this.isManageLibraryPage || !requested) return;
      if (requested === 'data') {
        untracked(() => void this.router?.navigate(['/settings/library'], { replaceUrl: true }));
        return;
      }
      if (!LINKABLE_SECTIONS.includes(requested)) return;
      // A link to a section this deployment does not have leaves the default open.
      if (requested === 'connections' && !this.canConfigureAiProvider()) return;
      if (requested === 'external' && !this.supportsExternalMediaLibraries()) return;
      if (requested === 'apps' && !this.supportsMcpConnectedApps()) return;
      if (requested === 'devices' && !this.supportsCarrelDevices()) return;
      if (requested === 'account' && !this.isCloud()) return;
      untracked(() => this.showSection(requested));
    });

    // `?open=calibre` opens the Calibre import directly, so the upload and
    // review can be linked to (support replies, the release notes and the
    // visual QA screenshots). Dropped once used, like `open=applebooks`.
    effect(() => {
      if (this.isManageLibraryPage || this.queryParamMap().get('open') !== 'calibre') return;
      if (!this.supportsCalibre()) return;
      untracked(() => {
        this.showSection('import');
        this.openCalibreImport();
        void this.router?.navigate([], {
          relativeTo: this.route,
          queryParams: { open: null },
          queryParamsHandling: 'merge',
          replaceUrl: true,
        });
      });
    });

    // `?section=import&open=applebooks` opens straight on Apple Books' steps,
    // so help pages and support replies can link to them (#1357). The
    // parameter is dropped once used, so a reload does not reopen it.
    effect(() => {
      if (this.isManageLibraryPage || this.queryParamMap().get('open') !== 'applebooks') return;
      untracked(() => {
        this.showSection('import');
        this.openHighlightImport('applebooks');
        void this.router?.navigate([], {
          relativeTo: this.route,
          queryParams: { open: null },
          queryParamsHandling: 'merge',
          replaceUrl: true,
        });
      });
    });

    effect(() => {
      this.deploymentCapabilities();
      this.activeSettingsSection();
      // The narrow layout scrolls its sections sideways; keep the open one in view.
      setTimeout(() =>
        this.host.nativeElement
          .querySelector('.settings-nav-item.is-active')
          ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }),
      );
    });
  }

  ngOnInit(): void {
    this.loadCapabilities();
  }

  openHighlightImport(start: HighlightImportStart = 'all'): void {
    this.highlightImport.open(start);
  }

  openCalibreImport(): void {
    this.calibreImport.open();
  }

  setSettingsSection(section: SettingsSection): void {
    if (section === 'data') {
      if (!this.isManageLibraryPage) void this.router?.navigate(['/settings/library']);
      return;
    }
    if (this.isManageLibraryPage) {
      void this.router?.navigate(['/settings'], { queryParams: { section } });
      return;
    }

    this.showSection(section);
    void this.router?.navigate([], {
      relativeTo: this.route,
      queryParams: { section },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** "Try again" after the capability read failed: back to the loading state. */
  retryCapabilities(): void {
    this.loadCapabilities(true);
  }

  showAccountDeletionPending(status: CloudAccountDeletionStatus): void {
    this.cloudEntry.showAccountDeletionPending(status);
  }

  /**
   * Capability-on only: a completed shared import replaced the server library,
   * so refresh what Settings reads from the server rather than leaving stale
   * data on screen.
   */
  onLibraryTransferCompleted(): void {
    if (this.isFirstRunImport() || this.cloudEntry.firstRunImportPending()) {
      this.cloudEntry.finishFirstRunAfterImport();
    }
    this.localBackup()?.reload();
  }

  private showSection(section: SettingsSection): void {
    this.activeSettingsSection.set(section);
  }

  private loadCapabilities(refresh = false): void {
    // Sections mount their own cards after this resolves, so no owner or
    // infrastructure API is called before the server says this deployment
    // exposes it — and none flashes while the request is in flight.
    this.deploymentCapabilitiesService.get(refresh).subscribe({
      error: () => {},
    });
  }
}
