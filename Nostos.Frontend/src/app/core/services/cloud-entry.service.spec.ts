import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { CloudAuthService } from './cloud-auth.service';
import { CloudEntryService } from './cloud-entry.service';
import { CloudOnboardingService } from './cloud-onboarding.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { PortableLibraryService } from './portable-library.service';

describe('CloudEntryService', () => {
  let service: CloudEntryService;
  let capabilities: { get: ReturnType<typeof vi.fn> };
  let auth: {
    getSession: ReturnType<typeof vi.fn>;
    loginUrl: ReturnType<typeof vi.fn>;
  };
  let onboarding: {
    getState: ReturnType<typeof vi.fn>;
    provision: ReturnType<typeof vi.fn>;
    createCheckout: ReturnType<typeof vi.fn>;
    reconcileSubscription: ReturnType<typeof vi.fn>;
    createBillingPortal: ReturnType<typeof vi.fn>;
  };

  const cloudCapabilities = {
    deploymentMode: 'Cloud' as const,
    requiresAuthentication: true,
    canConfigureAiProvider: false,
    managedAi: true,
    managedVoiceTranscription: true,
    usesCloudStorage: true,
    supportsLocalBackupConfiguration: false,
    supportsPrivateNetworkAccess: false,
    usageMeteringAvailable: true,
  };

  const session = {
    authenticated: true,
    accountState: 'Unknown' as const,
    account: {
      id: '1e4df713-1a34-4fc7-9a90-c45169256845',
      displayName: 'Reader',
      email: 'reader@example.test',
    },
  };

  beforeEach(() => {
    localStorage.clear();
    history.replaceState({}, '', '/');

    capabilities = { get: vi.fn() };
    auth = { getSession: vi.fn(), loginUrl: vi.fn() };
    onboarding = {
      getState: vi.fn(),
      provision: vi.fn(),
      createCheckout: vi.fn(),
      reconcileSubscription: vi.fn(),
      createBillingPortal: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        CloudEntryService,
        { provide: DeploymentCapabilitiesService, useValue: capabilities },
        { provide: CloudAuthService, useValue: auth },
        { provide: CloudOnboardingService, useValue: onboarding },
        {
          provide: PortableLibraryService,
          useValue: { importArchive: vi.fn().mockReturnValue(of({})) },
        },
      ],
    });

    service = TestBed.inject(CloudEntryService);
  });

  it('leaves SelfHosted on the normal product path without Cloud auth', async () => {
    capabilities.get.mockReturnValue(of({
      ...cloudCapabilities,
      deploymentMode: 'SelfHosted',
      requiresAuthentication: false,
    }));

    await service.initialize();

    expect(service.productReady()).toBe(true);
    expect(auth.getSession).not.toHaveBeenCalled();
    expect(onboarding.getState).not.toHaveBeenCalled();
  });

  it('shows the hosted sign-in path for a signed-out Cloud user', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({
      authenticated: false,
      accountState: null,
      account: null,
    }));
    auth.loginUrl.mockReturnValue('/api/auth/login?returnUrl=%2Flibrary');

    await service.initialize();

    expect(service.view().kind).toBe('signed_out');
    expect(service.productReady()).toBe(false);

    expect(service.loginUrl()).toBe('/api/auth/login?returnUrl=%2Flibrary');
    expect(auth.loginUrl).toHaveBeenCalledWith('/', null);
  });

  it('passes prompt=login to auth service when returning from an explicit sign out', () => {
    history.replaceState({}, '', '/start?signedOut=true');

    service.loginUrl();
    expect(auth.loginUrl).toHaveBeenCalledWith('/start?signedOut=true', null, 'login');
  });

  it('preserves a raw start offer through authentication without trusting it as checkout state', async () => {
    history.replaceState({}, '', '/start?offer=Pro-Annual');
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({
      authenticated: false,
      accountState: null,
      account: null,
    }));
    auth.loginUrl.mockReturnValue(
      '/api/auth/login?returnUrl=%2Fstart%3Foffer%3DPro-Annual',
    );

    await service.initialize();

    expect(service.loginUrl()).toBe(
      '/api/auth/login?returnUrl=%2Fstart%3Foffer%3DPro-Annual',
    );
    expect(auth.loginUrl).toHaveBeenCalledWith('/start?offer=Pro-Annual', 'Pro-Annual');
    expect(service.selectedOffer()).toBeNull();
  });

  it('uses the server-validated canonical offer for checkout', async () => {
    history.replaceState({}, '', '/start?offer=Pro-Annual');
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'pro-annual',
        planName: 'Pro',
        billingCadence: 'Annual',
      },
    }));
    onboarding.createCheckout.mockReturnValue(of({
      url: 'https://checkout.example.test/session',
    }));

    await service.initialize();

    expect(onboarding.getState).toHaveBeenCalledWith('Pro-Annual');
    expect(service.selectedOffer()).toEqual({
      offerId: 'pro-annual',
      planName: 'Pro',
      billingCadence: 'Annual',
    });

    const url = await service.beginCheckout(service.selectedOffer()?.offerId ?? null);

    expect(url).toBe('https://checkout.example.test/session');
    expect(onboarding.createCheckout).toHaveBeenCalledWith('pro-annual');
  });

  it('passes the selected offer to reconcileSubscription so the selection is retained across checks', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'checkout_pending',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      },
    }));
    onboarding.reconcileSubscription.mockReturnValue(of({
      state: 'checkout_pending',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      },
    }));

    await service.initialize();
    expect(service.selectedOffer()?.offerId).toBe('standard-monthly');

    await service.checkSubscription();
    expect(onboarding.reconcileSubscription).toHaveBeenCalledWith('standard-monthly');
    expect(service.selectedOffer()?.offerId).toBe('standard-monthly');
  });

  it('does not submit an unvalidated browser offer to checkout', async () => {
    history.replaceState({}, '', '/start?offer=pro-annual');
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: null,
    }));

    await service.initialize();

    expect(await service.beginCheckout('pro-annual')).toBeNull();
    expect(onboarding.createCheckout).not.toHaveBeenCalled();
    expect(service.actionError()).toContain('valid Cloud plan');
  });

  it('keeps the selected offer across a refresh of the start URL', async () => {
    history.replaceState({}, '', '/start?offer=standard-annual');
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'standard-annual',
        planName: 'Standard',
        billingCadence: 'Annual',
      },
    }));

    await service.initialize();
    await service.initialize(true);

    expect(onboarding.getState).toHaveBeenNthCalledWith(1, 'standard-annual');
    expect(onboarding.getState).toHaveBeenNthCalledWith(2, 'standard-annual');
    expect(service.selectedOffer()?.offerId).toBe('standard-annual');
  });

  it('never flashes the normal app while subscription access is missing', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
    }));

    await service.initialize();

    expect(service.view().kind).toBe('subscription_required');
    expect(service.productReady()).toBe(false);
  });

  it('starts idempotent provisioning for an entitled new account', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'ready_to_provision',
      subscriptionStatus: 'Trial',
      ready: false,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: true,
    }));
    onboarding.provision.mockReturnValue(of({
      state: 'ready',
      subscriptionStatus: 'Trial',
      ready: true,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
    }));

    await service.initialize();

    expect(onboarding.provision).toHaveBeenCalledTimes(1);
    expect(service.view().kind).toBe('first_run');
    expect(service.productReady()).toBe(false);

    service.startFresh();
    expect(service.productReady()).toBe(true);
  });

  it('resumes polling from server state after refresh during provisioning', async () => {
    vi.useFakeTimers();

    try {
      capabilities.get.mockReturnValue(of(cloudCapabilities));
      auth.getSession.mockReturnValue(of(session));
      onboarding.getState
        .mockReturnValueOnce(
          of({
            state: 'provisioning',
            subscriptionStatus: 'Active',
            ready: false,
            canCheckout: false,
            canCheckSubscription: false,
            canManageSubscription: false,
            canRetry: false,
          }),
        )
        .mockReturnValueOnce(
          of({
            state: 'ready',
            subscriptionStatus: 'Active',
            ready: true,
            canCheckout: false,
            canCheckSubscription: false,
            canManageSubscription: false,
            canRetry: false,
          }),
        );

      await service.initialize();

      expect(service.view().kind).toBe('provisioning');
      expect(onboarding.provision).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(1500);

      expect(service.productReady()).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('ready returning accounts enter the normal app directly', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({
      ...session,
      accountState: 'Active',
    }));
    onboarding.getState.mockReturnValue(of({
      state: 'ready',
      subscriptionStatus: 'Active',
      ready: true,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
    }));

    await service.initialize();

    expect(service.productReady()).toBe(true);
    expect(service.view().kind).toBe('product');
  });

  it('fails closed on the migration capability and finishes first run after a shared import', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({ ...session, accountState: 'Active' }));
    onboarding.getState.mockReturnValue(of({
      state: 'ready',
      subscriptionStatus: 'Active',
      ready: true,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: false,
    }));

    await service.initialize();
    expect(service.supportsLibraryMigration()).toBe(false);
    expect(service.supportsSafeActivation()).toBe(false);

    capabilities.get.mockReturnValue(
      of({
        ...cloudCapabilities,
        supportsLibraryMigration: true,
        supportsSafeActivation: true,
      }),
    );
    localStorage.setItem('nostos.cloud.first-run.1e4df713-1a34-4fc7-9a90-c45169256845', 'pending');

    await service.initialize(true);

    expect(service.supportsLibraryMigration()).toBe(true);
    expect(service.view().kind).toBe('first_run');

    service.finishFirstRunAfterImport();

    expect(service.view().kind).toBe('product');
    expect(service.productReady()).toBe(true);
    expect(
      localStorage.getItem('nostos.cloud.first-run.1e4df713-1a34-4fc7-9a90-c45169256845'),
    ).toBeNull();
  });

  it('does not let disabled accounts enter or trigger provisioning', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({
      ...session,
      accountState: 'Disabled',
    }));

    await service.initialize();

    expect(service.view().kind).toBe('account_unavailable');
    expect(onboarding.getState).not.toHaveBeenCalled();
    expect(onboarding.provision).not.toHaveBeenCalled();
  });

  it('treats deletion-requested accounts as unavailable without provisioning', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of({
      ...session,
      accountState: 'DeletionRequested',
    }));

    await service.initialize();

    expect(service.view().kind).toBe('account_unavailable');
    expect(onboarding.getState).not.toHaveBeenCalled();
    expect(onboarding.provision).not.toHaveBeenCalled();
  });

  it('auto-advances to checkout when visiting /start with valid offer and canCheckout', async () => {
    history.replaceState({}, '', '/start?offer=standard-monthly');
    sessionStorage.clear();

    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      },
    }));
    onboarding.createCheckout.mockReturnValue(of({ url: 'https://nostos.page/pay?_ptxn=auto_123' }));

    await service.initialize();

    expect(service.checkoutRedirect()).toBe('https://nostos.page/pay?_ptxn=auto_123');
    expect(onboarding.createCheckout).toHaveBeenCalledWith('standard-monthly');
    expect(sessionStorage.getItem('nostos_cloud_auto_checkout_1e4df713-1a34-4fc7-9a90-c45169256845_standard-monthly')).toBe('attempted');
  });

  it('auto-advances to checkout with canonical offer ID even if query case differs', async () => {
    history.replaceState({}, '', '/start?offer=Pro-Annual');
    sessionStorage.clear();

    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'pro-annual',
        planName: 'Pro',
        billingCadence: 'Annual',
      },
    }));
    onboarding.createCheckout.mockReturnValue(of({ url: 'https://nostos.page/pay?_ptxn=canonical_pro' }));

    await service.initialize();

    expect(service.checkoutRedirect()).toBe('https://nostos.page/pay?_ptxn=canonical_pro');
    expect(onboarding.createCheckout).toHaveBeenCalledWith('pro-annual');
    expect(sessionStorage.getItem('nostos_cloud_auto_checkout_1e4df713-1a34-4fc7-9a90-c45169256845_pro-annual')).toBe('attempted');
  });

  it('does not auto-advance when returning to /start if sessionStorage marker is already set', async () => {
    history.replaceState({}, '', '/start?offer=standard-monthly');
    sessionStorage.setItem('nostos_cloud_auto_checkout_1e4df713-1a34-4fc7-9a90-c45169256845_standard-monthly', 'attempted');

    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_required',
      subscriptionStatus: 'None',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      },
    }));

    await service.initialize();

    expect(service.checkoutRedirect()).toBeNull();
    expect(onboarding.createCheckout).not.toHaveBeenCalled();
    expect(service.view().kind).toBe('subscription_required');
  });

  it('maps a pending checkout cleanly to subscription_required with canCheckSubscription intact', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'checkout_pending',
      subscriptionStatus: 'Pending',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: {
        offerId: 'pro-annual',
        planName: 'Pro',
        billingCadence: 'Annual',
      },
    }));

    await service.initialize();

    expect(service.view().kind).toBe('subscription_required');
    expect(service.view().onboarding?.canCheckSubscription).toBe(true);
    expect(service.selectedOffer()?.planName).toBe('Pro');
    expect(service.productReady()).toBe(false);
  });

  it('keeps the legacy pending state cleanly on the subscription_required path', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'subscription_pending',
      subscriptionStatus: 'Pending',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: null,
    }));

    await service.initialize();

    expect(service.view().kind).toBe('subscription_required');
    expect(service.productReady()).toBe(false);
  });

  it('routes past-due billing to payment recovery instead of fully inactive', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'past_due',
      subscriptionStatus: 'PastDue',
      ready: false,
      canCheckout: false,
      canCheckSubscription: true,
      canManageSubscription: true,
      canRetry: false,
      selectedOffer: {
        offerId: 'pro-annual',
        planName: 'Pro',
        billingCadence: 'Annual',
      },
    }));

    await service.initialize();

    expect(service.view().kind).toBe('payment_recovery');
    expect(service.productReady()).toBe(false);
  });

  it('classifies legacy inactive snapshots by subscription status', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));

    onboarding.getState.mockReturnValue(of({
      state: 'subscription_inactive',
      subscriptionStatus: 'PastDue',
      ready: false,
      canCheckout: false,
      canCheckSubscription: true,
      canManageSubscription: true,
      canRetry: false,
      selectedOffer: null,
    }));
    await service.initialize();
    expect(service.view().kind).toBe('payment_recovery');

    onboarding.getState.mockReturnValue(of({
      state: 'subscription_inactive',
      subscriptionStatus: 'Canceled',
      ready: false,
      canCheckout: true,
      canCheckSubscription: true,
      canManageSubscription: false,
      canRetry: false,
      selectedOffer: null,
    }));
    await service.initialize();
    expect(service.view().kind).toBe('canceled');

    onboarding.getState.mockReturnValue(of({
      state: 'subscription_inactive',
      subscriptionStatus: 'Inactive',
      ready: false,
      canCheckout: false,
      canCheckSubscription: true,
      canManageSubscription: true,
      canRetry: false,
      selectedOffer: null,
    }));
    await service.initialize();
    expect(service.view().kind).toBe('inactive');
    expect(service.productReady()).toBe(false);
  });

  it('retries failed provisioning through the idempotent provision call', async () => {
    capabilities.get.mockReturnValue(of(cloudCapabilities));
    auth.getSession.mockReturnValue(of(session));
    onboarding.getState.mockReturnValue(of({
      state: 'provisioning_failed',
      subscriptionStatus: 'Active',
      ready: false,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: true,
      selectedOffer: null,
    }));
    onboarding.provision.mockReturnValue(of({
      state: 'provisioning',
      subscriptionStatus: 'Active',
      ready: false,
      canCheckout: false,
      canCheckSubscription: false,
      canManageSubscription: false,
      canRetry: true,
      selectedOffer: null,
    }));

    await service.initialize();
    expect(service.view().kind).toBe('provisioning_failed');

    await service.retry();
    expect(onboarding.provision).toHaveBeenCalledTimes(1);
    expect(service.view().kind).toBe('provisioning');
  });
});
