import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { CloudOnboardingSnapshot } from '../dtos/cloud-onboarding.dtos';
import { CloudSession } from '../dtos/cloud-auth.dtos';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { CloudAuthService } from './cloud-auth.service';
import { CloudOnboardingService } from './cloud-onboarding.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { PortableLibraryService } from './portable-library.service';

export type CloudEntryKind =
  | 'loading'
  | 'signed_out'
  | 'subscription_required'
  | 'first_run'
  | 'product'
  | 'provisioning'
  | 'provisioning_failed'
  | 'payment_recovery'
  | 'canceled'
  | 'inactive'
  | 'account_unavailable'
  | 'backend_error';

export interface CloudEntryView {
  kind: CloudEntryKind;
  onboarding?: CloudOnboardingSnapshot;
}

/**
 * Single browser orchestration boundary for hosted Cloud entry.
 *
 * The backend remains authoritative for authentication, entitlement and
 * provisioning. Browser persistence is used only for the optional first-run
 * welcome/import choice after this browser has initiated provisioning; it
 * never selects a tenant or controls access.
 */
@Injectable({ providedIn: 'root' })
export class CloudEntryService {
  private readonly session = signal<CloudSession | null>(null);
  private readonly requestedOffer = signal<string | null>(null);
  private readonly deploymentCapabilities = signal<DeploymentCapabilities | null>(null);
  private hasAutoAdvancedCheckout = false;
  private pollHandle: ReturnType<typeof setTimeout> | undefined;

  readonly view = signal<CloudEntryView>({ kind: 'loading' });
  readonly actionPending = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly checkoutRedirect = signal<string | null>(null);
  readonly productReady = computed(() => this.view().kind === 'product');
  readonly selectedOffer = computed(() => this.view().onboarding?.selectedOffer ?? null);

  /**
   * Server-authoritative migration capability (#680 plan §4). False or absent
   * keeps the legacy first-run import; true mounts the shared import flow.
   * Never inferred from the deployment mode.
   */
  readonly supportsLibraryMigration = computed(
    () => this.deploymentCapabilities()?.supportsLibraryMigration === true,
  );

  constructor(
    private readonly capabilities: DeploymentCapabilitiesService,
    private readonly auth: CloudAuthService,
    private readonly onboarding: CloudOnboardingService,
    private readonly portableLibrary: PortableLibraryService,
  ) {}

  async initialize(force = false): Promise<void> {
    this.clearPoll();
    this.actionError.set(null);
    this.checkoutRedirect.set(null);
    this.requestedOffer.set(null);
    this.view.set({ kind: 'loading' });

    try {
      const capabilities = await firstValueFrom(this.capabilities.get(force));
      this.deploymentCapabilities.set(capabilities);
      if (capabilities.deploymentMode === 'SelfHosted') {
        this.session.set(null);
        this.view.set({ kind: 'product' });
        return;
      }

      this.requestedOffer.set(this.readOfferFromLocation());

      const session = await firstValueFrom(this.auth.getSession(force));
      this.session.set(session);

      if (!session.authenticated || !session.account) {
        this.view.set({ kind: 'signed_out' });
        return;
      }

      if (
        session.accountState === 'Disabled' ||
        session.accountState === 'Deleted' ||
        session.accountState === 'DeletionRequested'
      ) {
        this.view.set({ kind: 'account_unavailable' });
        return;
      }

      await this.refreshOnboarding();
    } catch {
      this.deploymentCapabilities.set(null);
      this.view.set({ kind: 'backend_error' });
    }
  }

  loginUrl(): string {
    const location = globalThis.location;
    const returnUrl = `${location.pathname}${location.search}${location.hash}` || '/';
    const isSignedOutState = new URLSearchParams(location.search).get('signedOut') === 'true';
    return isSignedOutState
      ? this.auth.loginUrl(returnUrl, this.requestedOffer(), 'login')
      : this.auth.loginUrl(returnUrl, this.requestedOffer());
  }

  async retry(): Promise<void> {
    if (this.view().kind === 'provisioning_failed' || this.view().kind === 'provisioning') {
      await this.startProvisioning();
      return;
    }

    if (this.view().kind === 'backend_error') {
      await this.initialize(true);
      return;
    }

    await this.refreshOnboarding();
  }

  async beginCheckout(offerId: string | null): Promise<string | null> {
    const selectedOffer = this.selectedOffer();
    if (!selectedOffer || !offerId || offerId !== selectedOffer.offerId) {
      this.actionError.set('Choose a valid Cloud plan before continuing to checkout.');
      return null;
    }

    if (!this.view().onboarding?.canCheckout) {
      return null;
    }

    this.actionPending.set(true);
    this.actionError.set(null);

    try {
      const redirect = await firstValueFrom(
        this.onboarding.createCheckout(selectedOffer.offerId),
      );
      return redirect.url;
    } catch {
      this.actionError.set('Checkout is temporarily unavailable. Try again.');
      return null;
    } finally {
      this.actionPending.set(false);
    }
  }

  async checkSubscription(): Promise<void> {
    this.actionPending.set(true);
    this.actionError.set(null);

    const offerId = this.selectedOffer()?.offerId ?? this.requestedOffer();

    try {
      const snapshot = await firstValueFrom(this.onboarding.reconcileSubscription(offerId));
      await this.applyOnboarding(snapshot);
    } catch {
      this.actionError.set("We couldn't check your subscription right now. Try again.");
    } finally {
      this.actionPending.set(false);
    }
  }

  async openBillingPortal(): Promise<string | null> {
    this.actionPending.set(true);
    this.actionError.set(null);

    try {
      const redirect = await firstValueFrom(this.onboarding.createBillingPortal());
      return redirect.url;
    } catch {
      this.actionError.set('Subscription management is temporarily unavailable. Try again.');
      return null;
    } finally {
      this.actionPending.set(false);
    }
  }

  startFresh(): void {
    this.completeFirstRun();
  }

  /**
   * Completion handoff for the shared import flow: the server job (or the
   * activation slice) reported a finished import, so the first-run marker can
   * be cleared and the product entered. Failures never call this, so a failed
   * transfer keeps the first-run choice on screen.
   */
  finishFirstRunAfterImport(): void {
    this.actionError.set(null);
    this.completeFirstRun();
  }

  async importPortableArchive(file: File): Promise<void> {
    this.actionPending.set(true);
    this.actionError.set(null);

    try {
      await firstValueFrom(this.portableLibrary.importArchive(file));
      this.completeFirstRun();
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 409) {
        this.actionError.set(
          'This Cloud library already contains data, so the archive was not imported.',
        );
      } else if (error instanceof HttpErrorResponse && error.status === 400) {
        this.actionError.set('That file could not be imported as a Nostos portable library.');
      } else {
        this.actionError.set("We couldn't import that library right now. Try again.");
      }
    } finally {
      this.actionPending.set(false);
    }
  }

  private async refreshOnboarding(): Promise<void> {
    this.clearPoll();

    try {
      const snapshot = await firstValueFrom(this.onboarding.getState(this.requestedOffer()));
      await this.applyOnboarding(snapshot);
    } catch {
      this.view.set({ kind: 'backend_error' });
    }
  }

  private async startProvisioning(): Promise<void> {
    this.clearPoll();
    this.markFirstRunPending();
    this.view.set({ kind: 'provisioning' });
    this.actionError.set(null);

    try {
      const snapshot = await firstValueFrom(this.onboarding.provision());
      await this.applyOnboarding(snapshot);
    } catch {
      // A dropped browser request does not own provisioning lifetime on the
      // server. Re-reading the control plane is the safe recovery path.
      this.view.set({ kind: 'backend_error' });
    }
  }

  private async applyOnboarding(snapshot: CloudOnboardingSnapshot): Promise<void> {
    switch (snapshot.state) {
      case 'ready':
        this.clearPoll();
        this.view.set({
          kind: this.hasFirstRunPending() ? 'first_run' : 'product',
          onboarding: snapshot,
        });
        return;

      case 'ready_to_provision':
        await this.startProvisioning();
        return;

      case 'provisioning':
        this.view.set({ kind: 'provisioning', onboarding: snapshot });
        this.schedulePoll();
        return;

      case 'provisioning_failed':
        this.clearPoll();
        this.view.set({ kind: 'provisioning_failed', onboarding: snapshot });
        return;

      case 'subscription_required':
      case 'subscription_pending':
      case 'checkout_pending':
        this.clearPoll();
        this.view.set({ kind: 'subscription_required', onboarding: snapshot });
        void this.maybeAutoAdvanceCheckout(snapshot);
        return;

      case 'grace':
      case 'past_due':
        this.clearPoll();
        this.view.set({ kind: 'payment_recovery', onboarding: snapshot });
        return;

      case 'canceled':
        this.clearPoll();
        this.view.set({ kind: 'canceled', onboarding: snapshot });
        return;

      case 'subscription_inactive':
      case 'inactive':
        this.clearPoll();
        this.view.set({ kind: this.classifyInactive(snapshot), onboarding: snapshot });
        return;

      case 'account_unavailable':
        this.clearPoll();
        this.view.set({ kind: 'account_unavailable', onboarding: snapshot });
        return;
    }
  }

  private async maybeAutoAdvanceCheckout(snapshot: CloudOnboardingSnapshot): Promise<void> {
    if (this.hasAutoAdvancedCheckout) return;

    // Only auto-advance if user explicitly arrived with an offer,
    // the backend resolved a valid selectedOffer, and checkout is permitted.
    const explicitOffer = this.requestedOffer();
    if (!explicitOffer || !snapshot.canCheckout || !snapshot.selectedOffer) return;

    // State alignment: Backend CreateCheckoutAsync accepts subscription_required or subscription_pending
    if (snapshot.state !== 'subscription_required' && snapshot.state !== 'subscription_pending') {
      return;
    }

    const canonicalOfferId = snapshot.selectedOffer.offerId;
    const accountId = this.session()?.account?.id ?? 'anonymous';
    const storageKey = `nostos_cloud_auto_checkout_${accountId}_${canonicalOfferId}`;

    try {
      if (globalThis.sessionStorage?.getItem(storageKey) === 'attempted') {
        return;
      }
      globalThis.sessionStorage?.setItem(storageKey, 'attempted');
    } catch {
      // If sessionStorage is unavailable or throws, fail-safe: do not auto-redirect
      return;
    }

    this.hasAutoAdvancedCheckout = true;
    const url = await this.beginCheckout(canonicalOfferId);

    // Verify state has not drifted or changed to product while awaiting checkout creation
    if (url && this.view().kind === 'subscription_required') {
      this.checkoutRedirect.set(url);
    }
  }

  private classifyInactive(snapshot: CloudOnboardingSnapshot): 'payment_recovery' | 'canceled' | 'inactive' {
    const status = (snapshot.subscriptionStatus ?? '').toLowerCase().replace(/[\s_]+/g, '');
    if (status.includes('pastdue') || status.includes('grace') || status.includes('past_due')) {
      return 'payment_recovery';
    }
    if (status.includes('cancel')) {
      return 'canceled';
    }
    return 'inactive';
  }

  private readOfferFromLocation(): string | null {
    const location = globalThis.location;
    if (location.pathname !== '/start' && location.pathname !== '/start/') return null;

    return new URLSearchParams(location.search).get('offer');
  }

  private schedulePoll(): void {
    this.clearPoll();
    this.pollHandle = setTimeout(() => {
      void this.refreshOnboarding();
    }, 1500);
  }

  private clearPoll(): void {
    if (this.pollHandle !== undefined) {
      clearTimeout(this.pollHandle);
      this.pollHandle = undefined;
    }
  }

  private markerKey(): string | null {
    const accountId = this.session()?.account?.id;
    return accountId ? `nostos.cloud.first-run.${accountId}` : null;
  }

  private markFirstRunPending(): void {
    const key = this.markerKey();
    if (!key) return;

    try {
      localStorage.setItem(key, 'pending');
    } catch {
      // Optional UX persistence only. Access/provisioning never depends on it.
    }
  }

  private hasFirstRunPending(): boolean {
    const key = this.markerKey();
    if (!key) return false;

    try {
      return localStorage.getItem(key) === 'pending';
    } catch {
      return false;
    }
  }

  private completeFirstRun(): void {
    const key = this.markerKey();
    if (key) {
      try {
        localStorage.removeItem(key);
      } catch {
        // Optional UX persistence only.
      }
    }

    this.actionError.set(null);
    this.view.set({ kind: 'product' });
  }
}
