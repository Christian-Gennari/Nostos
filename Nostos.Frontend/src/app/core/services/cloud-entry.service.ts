import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { CloudOnboardingSnapshot } from '../dtos/cloud-onboarding.dtos';
import { CloudSession } from '../dtos/cloud-auth.dtos';
import { CloudAccountDeletionStatus } from '../dtos/cloud-account-deletion.dtos';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { CloudAccountDeletionService } from './cloud-account-deletion.service';
import { BooksService } from './books.service';
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
  | 'account_deletion'
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
  readonly firstRunImportPending = signal(false);
  readonly accountDeletionStatus = signal<CloudAccountDeletionStatus | null>(null);
  readonly accountDeletionStatusLoading = signal(false);
  readonly accountDeletionStatusFailed = signal(false);
  readonly accountDeletionActionPending = signal(false);
  readonly accountDeletionActionError = signal<string | null>(null);
  readonly accountDeletionSessionExpired = signal(false);
  readonly deletionExportUrl = computed(() => {
    const status = this.accountDeletionStatus();
    return status ? status.portableExportUrl : '/api/portability/export';
  });
  readonly canCancelDeletion = computed(
    () => this.accountDeletionStatus()?.canCancel ?? true,
  );
  readonly accountManagementUrl = computed(
    () => this.deploymentCapabilities()?.accountManagementUrl ?? null,
  );
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

  /**
   * Server-authoritative safe activation (#681). The shared import flow only
   * starts activation when this is true; false keeps the gated explanation.
   */
  readonly supportsSafeActivation = computed(
    () => this.deploymentCapabilities()?.supportsSafeActivation === true,
  );

  constructor(
    private readonly capabilities: DeploymentCapabilitiesService,
    private readonly auth: CloudAuthService,
    private readonly onboarding: CloudOnboardingService,
    private readonly portableLibrary: PortableLibraryService,
    private readonly books: BooksService,
    private readonly accountDeletion: CloudAccountDeletionService,
  ) {}

  async initialize(force = false): Promise<void> {
    this.clearPoll();
    this.actionError.set(null);
    this.checkoutRedirect.set(null);
    this.requestedOffer.set(null);
    this.firstRunImportPending.set(false);
    this.accountDeletionStatus.set(null);
    this.accountDeletionStatusLoading.set(false);
    this.accountDeletionStatusFailed.set(false);
    this.accountDeletionActionPending.set(false);
    this.accountDeletionActionError.set(null);
    this.accountDeletionSessionExpired.set(false);
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

      if (session.accountState === 'DeletionRequested') {
        if (capabilities.supportsAccountDeletion === true) {
          this.view.set({ kind: 'account_deletion' });
          await this.refreshAccountDeletionStatus();
        } else {
          this.view.set({ kind: 'account_unavailable' });
        }
        return;
      }

      if (session.accountState === 'Disabled' || session.accountState === 'Deleted') {
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

  showAccountDeletionPending(status: CloudAccountDeletionStatus): void {
    this.accountDeletionStatus.set(status);
    this.accountDeletionStatusFailed.set(false);
    this.accountDeletionActionError.set(null);
    this.accountDeletionSessionExpired.set(false);
    this.view.set({ kind: status.state === 'Deleted' ? 'account_unavailable' : 'account_deletion' });
  }

  async retryAccountDeletionStatus(): Promise<void> {
    this.accountDeletionActionError.set(null);
    this.accountDeletionSessionExpired.set(false);
    await this.refreshAccountDeletionStatus();
  }

  async cancelAccountDeletion(): Promise<void> {
    if (this.accountDeletionActionPending()) return;

    this.accountDeletionActionPending.set(true);
    this.accountDeletionActionError.set(null);
    this.accountDeletionSessionExpired.set(false);
    try {
      const status = await firstValueFrom(this.accountDeletion.cancelDeletion());
      if (status.state === 'Cancelled' || status.state === 'Active') {
        await this.initialize(true);
        return;
      }

      this.applyAccountDeletionStatus(status);
      if (!status.canCancel) {
        this.accountDeletionActionError.set(
          'The 14-day grace period has ended. This deletion can no longer be cancelled.',
        );
      }
    } catch (error) {
      this.accountDeletionActionError.set(this.accountDeletionFailureMessage(error));
      this.accountDeletionSessionExpired.set(
        error instanceof HttpErrorResponse && error.status === 401,
      );
      await this.refreshAccountDeletionStatus();
    } finally {
      this.accountDeletionActionPending.set(false);
    }
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

  /** Enters the product shell so first-run import can use Manage library. */
  openManageLibraryFromFirstRun(): void {
    const current = this.view();
    if (current.kind !== 'first_run') return;

    this.actionError.set(null);
    this.view.set({ kind: 'product', onboarding: current.onboarding });
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

  private async refreshAccountDeletionStatus(): Promise<void> {
    this.accountDeletionStatusLoading.set(true);
    this.accountDeletionStatusFailed.set(false);

    try {
      const status = await firstValueFrom(this.accountDeletion.getStatus());
      if (status.state === 'Cancelled' || status.state === 'Active') {
        this.accountDeletionStatus.set(status);
        await this.initialize(true);
        return;
      }

      this.applyAccountDeletionStatus(status);
    } catch (error) {
      this.accountDeletionStatusFailed.set(true);
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.accountDeletionSessionExpired.set(true);
      }
      this.view.set({ kind: 'account_deletion' });
    } finally {
      this.accountDeletionStatusLoading.set(false);
    }
  }

  private applyAccountDeletionStatus(status: CloudAccountDeletionStatus): void {
    this.accountDeletionStatus.set(status);
    this.accountDeletionStatusFailed.set(false);

    if (status.state === 'Deleted') {
      this.view.set({ kind: 'account_unavailable' });
      return;
    }

    this.view.set({ kind: 'account_deletion' });
  }

  private accountDeletionFailureMessage(error: unknown): string {
    const response = error instanceof HttpErrorResponse ? error : null;
    const code = typeof response?.error?.error === 'string' ? response.error.error : null;

    if (code === 'deletion_not_recoverable') {
      return 'The 14-day grace period has ended. This deletion can no longer be cancelled.';
    }
    if (code === 'account_lifecycle_busy') {
      return 'An account change is still in progress. Nostos could not confirm cancellation; check the status before trying again.';
    }
    if (response?.status === 401) {
      return 'Your sign-in session expired. Nostos could not confirm whether cancellation succeeded; sign in again and check the status.';
    }
    if (response?.status === 0) {
      return 'Nostos could not confirm whether cancellation succeeded. Check the status below before leaving this page.';
    }

    return 'Nostos could not confirm cancellation. Check the current deletion status before trying again.';
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

  /**
   * The first-run welcome/import choice is only for a library that is actually
   * empty. A schema upgrade of an existing tenant reuses the provisioning path
   * and leaves the browser marker pending, so a pending marker asks the server
   * for the library count: populated enters the product and clears the marker.
   * An unanswerable check (network/5xx/malformed) also enters the product but
   * leaves the marker, so a later visit can still offer the choice once the
   * server answers that the library really is empty. Never offer a destructive
   * choice on a guess.
   */
  private async resolveReadyKind(): Promise<'first_run' | 'product'> {
    const firstRunPending = this.hasFirstRunPending();
    this.firstRunImportPending.set(firstRunPending);
    if (!firstRunPending) return 'product';

    try {
      const counts = await firstValueFrom(this.books.getStatusCounts());
      if (typeof counts?.all !== 'number') return 'product';
      if (counts.all > 0) {
        this.clearFirstRunMarker();
        return 'product';
      }
      return 'first_run';
    } catch {
      return 'product';
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
          kind: await this.resolveReadyKind(),
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
      this.firstRunImportPending.set(true);
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
    this.clearFirstRunMarker();
    this.actionError.set(null);
    this.view.set({ kind: 'product' });
  }

  private clearFirstRunMarker(): void {
    this.firstRunImportPending.set(false);
    const key = this.markerKey();
    if (!key) return;

    try {
      localStorage.removeItem(key);
    } catch {
      // Optional UX persistence only.
    }
  }
}
