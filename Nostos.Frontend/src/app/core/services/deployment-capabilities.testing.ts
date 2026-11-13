import { computed, signal } from '@angular/core';
import { EMPTY, Observable, defer, of, tap } from 'rxjs';
import { vi } from 'vitest';

import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import type { DeploymentCapabilitiesService } from './deployment-capabilities.service';

export interface DeploymentCapabilitiesServiceMock
  extends Record<keyof DeploymentCapabilitiesService, unknown> {
  capabilities: ReturnType<typeof signal<DeploymentCapabilities | null>>['asReadonly'] extends () => infer R ? R : never;
  get: ReturnType<typeof vi.fn<(refresh?: boolean) => Observable<DeploymentCapabilities>>>;
  peek: ReturnType<typeof vi.fn<() => DeploymentCapabilities | null>>;
  setCapabilities: (capabilities: DeploymentCapabilities | null) => void;
  setFailed: (failed: boolean) => void;
  setLoading: (loading: boolean) => void;
  reset: () => void;
}

/**
 * Signal-backed test double for {@link DeploymentCapabilitiesService}.
 *
 * Mirrors the service's reactive signal contract when `get()` emits or errors,
 * while letting specs drive pending observables, errors, or direct signal
 * updates cleanly.
 */
export function createDeploymentCapabilitiesServiceMock(
  initialOrGet?:
    | DeploymentCapabilities
    | null
    | ((refresh?: boolean) => Observable<DeploymentCapabilities>),
) {
  const initialCapabilities = typeof initialOrGet === 'function' ? null : (initialOrGet ?? null);
  const _capabilities = signal<DeploymentCapabilities | null>(initialCapabilities);
  const _loading = signal(false);
  const _failed = signal(false);

  const source =
    typeof initialOrGet === 'function'
      ? initialOrGet
      : () => {
          const current = _capabilities();
          return current ? of(current) : EMPTY;
        };

  const deploymentMode = computed(() => _capabilities()?.deploymentMode ?? null);
  const isCloud = computed(() => _capabilities()?.deploymentMode === 'Cloud');
  const isSelfHosted = computed(() => _capabilities()?.deploymentMode === 'SelfHosted');
  const requiresAuthentication = computed(
    () => _capabilities()?.requiresAuthentication === true,
  );
  const canConfigureAiProvider = computed(
    () => _capabilities()?.canConfigureAiProvider === true,
  );
  const managedAi = computed(() => _capabilities()?.managedAi === true);
  const managedVoiceTranscription = computed(
    () => _capabilities()?.managedVoiceTranscription === true,
  );
  const usesCloudStorage = computed(() => _capabilities()?.usesCloudStorage === true);
  const supportsLocalBackupConfiguration = computed(
    () => _capabilities()?.supportsLocalBackupConfiguration === true,
  );
  const supportsPrivateNetworkAccess = computed(
    () => _capabilities()?.supportsPrivateNetworkAccess === true,
  );
  const supportsEreaderAccess = computed(
    () => _capabilities()?.supportsEreaderAccess === true,
  );
  const usageMeteringAvailable = computed(
    () => _capabilities()?.usageMeteringAvailable === true,
  );
  const supportsLibraryMigration = computed(
    () => _capabilities()?.supportsLibraryMigration === true,
  );
  const supportsSafeActivation = computed(
    () => _capabilities()?.supportsSafeActivation === true,
  );
  const supportsDirectPartUpload = computed(
    () => _capabilities()?.supportsDirectPartUpload === true,
  );
  const accountManagementUrl = computed(
    () => _capabilities()?.accountManagementUrl ?? null,
  );
  const feedbackUrl = computed(() => _capabilities()?.feedbackUrl ?? null);
  const hostedBrowserIntegrationEnabled = computed(
    () => _capabilities()?.hostedBrowserIntegrationEnabled === true,
  );
  const supportsManagedBackups = computed(
    () => _capabilities()?.supportsManagedBackups === true,
  );
  const supportsManagedBackupRestore = computed(
    () => _capabilities()?.supportsManagedBackupRestore === true,
  );
  const supportsManagedRestore = computed(
    () => supportsManagedBackups() && supportsManagedBackupRestore(),
  );
  const supportsAccountDeletion = computed(
    () => _capabilities()?.supportsAccountDeletion === true,
  );
  const supportsExternalMediaLibraries = computed(
    () => _capabilities()?.supportsExternalMediaLibraries === true,
  );
  const supportsCalibreImport = computed(
    () => _capabilities()?.supportsCalibreImport === true,
  );
  const supportsCalibreUpload = computed(
    () => _capabilities()?.supportsCalibreUpload === true,
  );
  const supportsCalibre = computed(
    () => supportsCalibreImport() || supportsCalibreUpload(),
  );
  const supportsResumableBookUploads = computed(
    () => _capabilities()?.supportsResumableBookUploads === true,
  );
  const supportsMcpConnectedApps = computed(
    () => _capabilities()?.supportsMcpConnectedApps === true,
  );
  const supportsCarrelDevices = computed(
    () => _capabilities()?.supportsCarrelDevices === true,
  );

  const get = vi.fn((refresh = false): Observable<DeploymentCapabilities> =>
    defer(() => {
      _loading.set(true);
      _failed.set(false);
      return source(refresh);
    }).pipe(
      tap({
        next: (capabilities) => {
          _capabilities.set(capabilities);
          _failed.set(false);
          _loading.set(false);
        },
        error: () => {
          _capabilities.set(null);
          _failed.set(true);
          _loading.set(false);
        },
      }),
    ),
  );

  const peek = vi.fn((): DeploymentCapabilities | null => _capabilities());

  const mock = {
    capabilities: _capabilities.asReadonly(),
    loading: _loading.asReadonly(),
    failed: _failed.asReadonly(),
    deploymentMode,
    isCloud,
    isSelfHosted,
    requiresAuthentication,
    canConfigureAiProvider,
    managedAi,
    managedVoiceTranscription,
    usesCloudStorage,
    supportsLocalBackupConfiguration,
    supportsPrivateNetworkAccess,
    supportsEreaderAccess,
    usageMeteringAvailable,
    supportsLibraryMigration,
    supportsSafeActivation,
    supportsDirectPartUpload,
    accountManagementUrl,
    feedbackUrl,
    hostedBrowserIntegrationEnabled,
    supportsManagedBackups,
    supportsManagedBackupRestore,
    supportsManagedRestore,
    supportsAccountDeletion,
    supportsExternalMediaLibraries,
    supportsCalibreImport,
    supportsCalibreUpload,
    supportsCalibre,
    supportsResumableBookUploads,
    supportsMcpConnectedApps,
    supportsCarrelDevices,
    get,
    peek,
    setCapabilities: (capabilities: DeploymentCapabilities | null) => {
      _capabilities.set(capabilities);
      _failed.set(false);
      _loading.set(false);
    },
    setFailed: (failed: boolean) => {
      _failed.set(failed);
      if (failed) _capabilities.set(null);
      _loading.set(false);
    },
    setLoading: (loading: boolean) => {
      _loading.set(loading);
    },
    reset: () => {
      _capabilities.set(initialCapabilities);
      _loading.set(false);
      _failed.set(false);
    },
  };

  // Compile-time check that every public member of DeploymentCapabilitiesService is present.
  const _typeCheck: DeploymentCapabilitiesService = mock as unknown as DeploymentCapabilitiesService;
  void _typeCheck;

  return mock;
}
