import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, defer, shareReplay, tap } from 'rxjs';

import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';

/**
 * Runtime deployment contract reported by the Nostos server.
 *
 * The Angular bundle is identical for SelfHosted and Cloud. Consumers use
 * these server-authoritative product capabilities instead of build flags,
 * hostname checks, or assumptions about infrastructure vendors.
 */
@Injectable({ providedIn: 'root' })
export class DeploymentCapabilitiesService {
  private readonly http = inject(HttpClient);
  private capabilities$?: Observable<DeploymentCapabilities>;
  private requestSeq = 0;

  private readonly _capabilities = signal<DeploymentCapabilities | null>(null);
  private readonly _loading = signal(false);
  private readonly _failed = signal(false);

  /** Latest server-authoritative capability manifest, or null before loaded / after failure. */
  readonly capabilities = this._capabilities.asReadonly();
  /** True while a capability request is currently in flight. */
  readonly loading = this._loading.asReadonly();
  /** True when the latest capability request failed. */
  readonly failed = this._failed.asReadonly();

  readonly deploymentMode = computed(() => this._capabilities()?.deploymentMode ?? null);
  readonly isCloud = computed(() => this._capabilities()?.deploymentMode === 'Cloud');
  readonly isSelfHosted = computed(() => this._capabilities()?.deploymentMode === 'SelfHosted');
  readonly requiresAuthentication = computed(
    () => this._capabilities()?.requiresAuthentication === true,
  );
  readonly canConfigureAiProvider = computed(
    () => this._capabilities()?.canConfigureAiProvider === true,
  );
  readonly managedAi = computed(() => this._capabilities()?.managedAi === true);
  readonly managedVoiceTranscription = computed(
    () => this._capabilities()?.managedVoiceTranscription === true,
  );
  readonly usesCloudStorage = computed(() => this._capabilities()?.usesCloudStorage === true);
  readonly supportsLocalBackupConfiguration = computed(
    () => this._capabilities()?.supportsLocalBackupConfiguration === true,
  );
  readonly supportsPrivateNetworkAccess = computed(
    () => this._capabilities()?.supportsPrivateNetworkAccess === true,
  );
  readonly supportsEreaderAccess = computed(
    () => this._capabilities()?.supportsEreaderAccess === true,
  );
  readonly usageMeteringAvailable = computed(
    () => this._capabilities()?.usageMeteringAvailable === true,
  );
  readonly supportsLibraryMigration = computed(
    () => this._capabilities()?.supportsLibraryMigration === true,
  );
  readonly supportsSafeActivation = computed(
    () => this._capabilities()?.supportsSafeActivation === true,
  );
  readonly supportsDirectPartUpload = computed(
    () => this._capabilities()?.supportsDirectPartUpload === true,
  );
  readonly accountManagementUrl = computed(
    () => this._capabilities()?.accountManagementUrl ?? null,
  );
  readonly feedbackUrl = computed(() => this._capabilities()?.feedbackUrl ?? null);
  readonly hostedBrowserIntegrationEnabled = computed(
    () => this._capabilities()?.hostedBrowserIntegrationEnabled === true,
  );
  readonly supportsManagedBackups = computed(
    () => this._capabilities()?.supportsManagedBackups === true,
  );
  readonly supportsManagedBackupRestore = computed(
    () => this._capabilities()?.supportsManagedBackupRestore === true,
  );
  readonly supportsManagedRestore = computed(
    () => this.supportsManagedBackups() && this.supportsManagedBackupRestore(),
  );
  readonly supportsAccountDeletion = computed(
    () => this._capabilities()?.supportsAccountDeletion === true,
  );
  readonly supportsExternalMediaLibraries = computed(
    () => this._capabilities()?.supportsExternalMediaLibraries === true,
  );
  readonly supportsCalibreImport = computed(
    () => this._capabilities()?.supportsCalibreImport === true,
  );
  readonly supportsCalibreUpload = computed(
    () => this._capabilities()?.supportsCalibreUpload === true,
  );
  readonly supportsCalibre = computed(
    () => this.supportsCalibreImport() || this.supportsCalibreUpload(),
  );
  readonly supportsResumableBookUploads = computed(
    () => this._capabilities()?.supportsResumableBookUploads === true,
  );
  readonly supportsMcpConnectedApps = computed(
    () => this._capabilities()?.supportsMcpConnectedApps === true,
  );
  readonly supportsCarrelDevices = computed(
    () => this._capabilities()?.supportsCarrelDevices === true,
  );

  get(refresh = false): Observable<DeploymentCapabilities> {
    if (refresh || !this.capabilities$) {
      const seq = ++this.requestSeq;
      this.capabilities$ = defer(() => {
        if (seq === this.requestSeq) {
          this._loading.set(true);
          this._failed.set(false);
        }
        return this.http.get<DeploymentCapabilities>('/api/runtime/capabilities');
      }).pipe(
        tap({
          next: (capabilities) => {
            if (seq !== this.requestSeq) return;
            this._capabilities.set(capabilities);
            this._failed.set(false);
            this._loading.set(false);
          },
          error: () => {
            if (seq !== this.requestSeq) return;
            this._capabilities.set(null);
            this._failed.set(true);
            this._loading.set(false);
            this.capabilities$ = undefined;
          },
        }),
        shareReplay({ bufferSize: 1, refCount: false }),
      );
    }

    return this.capabilities$;
  }

  /**
   * The answer already loaded by app bootstrap, without starting a request.
   *
   * A path decision that must be taken synchronously (which upload contract a
   * page will use) cannot wait on an observable. `null` means "not known yet";
   * callers fail closed to the older, always-present behaviour rather than
   * blocking a click.
   */
  peek(): DeploymentCapabilities | null {
    return this._capabilities();
  }
}
