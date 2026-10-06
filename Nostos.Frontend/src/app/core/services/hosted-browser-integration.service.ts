import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { distinctUntilChanged, take } from 'rxjs';

import { HostedBrowserIntegration, HostedBrowserSession } from '../dtos/hosted-browser-integration.dtos';
import { CloudAuthService } from './cloud-auth.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';

/** Optional host code; neither loading nor callbacks control app readiness. */
@Injectable({ providedIn: 'root' })
export class HostedBrowserIntegrationService {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly capabilities = inject(DeploymentCapabilitiesService);
  private readonly auth = inject(CloudAuthService);
  private started = false;
  private latestSession?: HostedBrowserSession;
  private integration?: HostedBrowserIntegration;

  start(): void {
    if (this.started) return;
    this.started = true;
    this.capabilities.get().pipe(take(1), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (capabilities) => {
        if (capabilities.deploymentMode !== 'Cloud' || !capabilities.hostedBrowserIntegrationEnabled) return;
        this.install();
      },
      error: () => {}, // An optional integration cannot hold up app startup.
    });
  }

  private install(): void {
    this.auth.sessionChanges$.pipe(
      distinctUntilChanged((a, b) => a.authenticated === b.authenticated && a.accountId === b.accountId),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((session) => {
      this.latestSession = session;
      this.notify();
    });

    const script = this.document.createElement('script');
    script.async = true;
    script.src = '/api/runtime/hosted-browser-integration.js';
    script.onload = () => {
      this.integration = this.document.defaultView?.nostosHostedBrowserIntegration;
      this.notify();
    };
    script.onerror = () => { this.integration = undefined; };
    this.document.head.appendChild(script);

    this.destroyRef.onDestroy(() => {
      script.onload = null;
      script.onerror = null;
      script.remove();
      this.integration = undefined;
    });
  }

  private notify(): void {
    if (!this.integration || !this.latestSession) return;
    try {
      // Give host code a copy so it cannot mutate the replayed auth state.
      this.integration.sessionChanged({ ...this.latestSession });
    } catch {
      // Host code owns its own failure/reset behavior; product navigation,
      // authentication and recovery entry must remain independent.
    }
  }
}
