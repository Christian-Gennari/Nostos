import { Injectable, computed, inject, signal } from '@angular/core';

import type { CloudManagedAiUsage } from '../../core/dtos/cloud-ai-refill.dtos';
import type { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';
import { CloudAiRefillService } from '../../core/services/cloud-ai-refill.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';

/**
 * The managed allowance as the assistant panel sees it (#1556).
 *
 * Settings already reads the same runtime capability and the same usage
 * endpoint. The panel needs both for the allowance failure: the existing
 * "Manage on nostos.page" account link (the place a refill is bought) and the
 * renewal date (so "used up for now" says when it stops being true).
 *
 * Everything is lazy and fails closed. A SelfHosted host never calls the
 * Cloud-only usage endpoint, and an unknown deployment leaves the panel on its
 * Settings fallback rather than guessing an account URL.
 */
@Injectable({ providedIn: 'root' })
export class AssistantAllowanceService {
  private readonly deployment = inject(DeploymentCapabilitiesService);
  private readonly refills = inject(CloudAiRefillService);

  private readonly capabilities = signal<DeploymentCapabilities | null>(
    this.deployment.peek(),
  );
  private capabilitiesLoading = false;

  private usageRequested = false;
  private usageLoaded = false;

  readonly usage = signal<CloudManagedAiUsage | null>(null);
  readonly usageFailed = signal(false);

  readonly canConfigureAiProvider = computed(
    () => this.capabilities()?.canConfigureAiProvider === true,
  );

  readonly accountManagementUrl = computed(
    () => this.capabilities()?.accountManagementUrl ?? null,
  );

  /**
   * The host advertises managed AI and metering. These two flags are the
   * contract for the usage endpoint; deployment mode is not consulted, so a
   * managed host is not hidden behind a name (and the evidence fixture can
   * simulate one without pretending to be Cloud).
   */
  readonly usageAvailable = computed(
    () =>
      this.capabilities()?.managedAi === true
      && this.capabilities()?.usageMeteringAvailable === true,
  );

  /** Load the runtime contract once, for the failure action's target. */
  ensureCapabilities(): void {
    if (this.capabilities() || this.capabilitiesLoading) return;

    this.capabilitiesLoading = true;
    this.deployment.get().subscribe({
      next: (capabilities) => {
        this.capabilities.set(capabilities);
        this.capabilitiesLoading = false;
        this.loadUsageIfReady();
      },
      error: () => {
        this.capabilitiesLoading = false;
      },
    });
  }

  /**
   * Ask for the allowance once an allowance failure is on screen.
   *
   * Idempotent: asking twice never opens a second usage request, and a host
   * that does not advertise managed metering never receives one at all.
   */
  ensureUsage(): void {
    this.usageRequested = true;
    this.ensureCapabilities();
    this.loadUsageIfReady();
  }

  private loadUsageIfReady(): void {
    if (!this.usageRequested || this.usageLoaded) return;
    if (!this.usageAvailable()) return;

    this.usageLoaded = true;
    this.refills.getUsage().subscribe({
      next: (usage) => {
        this.usage.set(usage);
        this.usageFailed.set(false);
      },
      error: () => {
        this.usageLoaded = false;
        this.usageFailed.set(true);
      },
    });
  }
}
