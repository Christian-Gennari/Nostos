import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Router } from '@angular/router';
import { vi } from 'vitest';
import { CloudEntryComponent } from './cloud-entry.component';
import { CloudEntryService, CloudEntryView } from '../core/services/cloud-entry.service';

describe('CloudEntryComponent', () => {
  let mockEntry: {
    view: ReturnType<typeof signal<CloudEntryView>>;
    actionPending: ReturnType<typeof signal<boolean>>;
    actionError: ReturnType<typeof signal<string | null>>;
    checkoutRedirect: ReturnType<typeof signal<string | null>>;
    productReady: ReturnType<typeof signal<boolean>>;
    selectedOffer: ReturnType<typeof signal<any>>;
    beginCheckout: ReturnType<typeof vi.fn>;
    checkSubscription: ReturnType<typeof vi.fn>;
    loginUrl: ReturnType<typeof vi.fn>;
    startFresh: ReturnType<typeof vi.fn>;
    openManageLibraryFromFirstRun: ReturnType<typeof vi.fn>;
  };
  const routerMock = { navigate: vi.fn() };

  beforeEach(() => {
    localStorage.clear();
    routerMock.navigate.mockClear();
    mockEntry = {
      view: signal<CloudEntryView>({
        kind: 'subscription_required',
        onboarding: {
          state: 'subscription_required',
          subscriptionStatus: 'None',
          ready: false,
          canCheckout: true,
          canCheckSubscription: false,
          canManageSubscription: false,
          canRetry: false,
          selectedOffer: {
            offerId: 'standard-monthly',
            planName: 'Standard',
            billingCadence: 'Monthly',
          },
        },
      }),
      actionPending: signal(false),
      actionError: signal<string | null>(null),
      checkoutRedirect: signal<string | null>(null),
      productReady: signal(false),
      selectedOffer: signal({
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      }),
      beginCheckout: vi.fn(),
      checkSubscription: vi.fn(),
      loginUrl: vi.fn().mockReturnValue('/api/auth/login'),
      startFresh: vi.fn(),
      openManageLibraryFromFirstRun: vi.fn(),
    };

    TestBed.configureTestingModule({
      imports: [CloudEntryComponent],
      providers: [
        { provide: CloudEntryService, useValue: mockEntry },
        { provide: Router, useValue: routerMock },
      ],
    });
  });

  function renderFirstRun(): void {
    mockEntry.view.set({ kind: 'first_run' });
  }

  function importChoice(
    fixture: ReturnType<typeof TestBed.createComponent>,
  ): HTMLButtonElement {
    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('button'),
    ) as HTMLButtonElement[];
    const choice = buttons.find((button) =>
      (button.textContent ?? '').includes('Import an existing Nostos library'),
    );
    if (!choice) throw new Error('Import choice button not found');
    return choice;
  }

  it('renders plan summary and active Continue to checkout button when selectedOffer is present', () => {
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Continue with Standard on Monthly billing');

    const button = compiled.querySelector('button.nostos-button--primary') as HTMLButtonElement;
    expect(button).toBeTruthy();
    expect(button.textContent).toContain('Continue to checkout');
    expect(button.disabled).toBe(false);

    button.click();
    expect(mockEntry.beginCheckout).toHaveBeenCalledWith('standard-monthly');
  });

  it('renders View Cloud plans link when selectedOffer is null (direct login)', () => {
    mockEntry.selectedOffer.set(null);
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_required',
        subscriptionStatus: 'None',
        ready: false,
        canCheckout: true,
        canCheckSubscription: false,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: null,
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Choose a plan on our pricing page to start your Cloud library');

    const link = compiled.querySelector('a.nostos-button--primary') as HTMLAnchorElement;
    expect(link).toBeTruthy();
    expect(link.textContent).toContain('View Cloud plans');
    expect(link.getAttribute('href')).toBe('https://nostos.page/pricing');
  });

  it('renders Check subscription button if canCheckSubscription is true without changing view state', () => {
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_pending',
        subscriptionStatus: 'Pending',
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
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const checkBtn = compiled.querySelector('button.nostos-button--secondary') as HTMLButtonElement;
    expect(checkBtn).toBeTruthy();
    expect(checkBtn.textContent).toContain('Check subscription');

    checkBtn.click();
    expect(mockEntry.checkSubscription).toHaveBeenCalledTimes(1);
  });

  it('omits Continue to checkout button when canCheckout is false', () => {
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_required',
        subscriptionStatus: 'None',
        ready: false,
        canCheckout: false,
        canCheckSubscription: true,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: {
          offerId: 'standard-monthly',
          planName: 'Standard',
          billingCadence: 'Monthly',
        },
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const checkoutBtn = compiled.querySelector('button.nostos-button--primary') as HTMLButtonElement;
    expect(checkoutBtn).toBeFalsy();

    const checkBtn = compiled.querySelector('button.nostos-button--secondary') as HTMLButtonElement;
    expect(checkBtn).toBeTruthy();
  });

  it('redirects when checkoutRedirect signal emits a url', () => {
    const fixture = TestBed.createComponent(CloudEntryComponent);
    const navigateSpy = vi.spyOn(fixture.componentInstance, 'navigateTo').mockImplementation(() => {});

    TestBed.flushEffects();

    mockEntry.checkoutRedirect.set('https://nostos.page/pay?_ptxn=txn_123');
    TestBed.flushEffects();

    expect(navigateSpy).toHaveBeenCalledWith('https://nostos.page/pay?_ptxn=txn_123');
  });

  it('routes first-run import through Manage library with import selected', () => {
    renderFirstRun();
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    expect(importChoice(fixture)).toBeTruthy();
    expect(fixture.nativeElement.querySelector('app-library-import-flow')).toBeNull();
    expect(fixture.nativeElement.querySelector('input[type="file"]')).toBeNull();

    importChoice(fixture).click();

    expect(mockEntry.openManageLibraryFromFirstRun).toHaveBeenCalledTimes(1);
    expect(routerMock.navigate).toHaveBeenCalledWith(['/settings/library'], {
      queryParams: { action: 'import', source: 'first-run' },
    });
  });
});
