import {
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  inject,
  signal,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';

import { ButtonComponent } from '../ui/button/button.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { ClerkSignInHost, CloudSignInService } from '../core/services/cloud-sign-in.service';

type SignInState = 'loading' | 'form' | 'continuing' | 'returning' | 'hosted';

/**
 * Within this window a second automatic hand-off is not attempted. If the
 * provider session exists but the Nostos cookie still did not stick (cookies
 * blocked, a failed callback), the reader gets a button instead of a loop.
 */
const AUTO_CONTINUE_WINDOW_MS = 60_000;
const AUTO_CONTINUE_KEY = 'nostos.cloud.signIn.autoContinuedAt';

/**
 * The signed-out Cloud screen's sign-in: the provider's form, embedded and
 * styled with the Nostos tokens, so signing in happens on the app's own page.
 *
 * - A provider session that is still alive (the Nostos cookie expired but the
 *   reader never signed out) continues straight into the app.
 * - Arriving from Nostos sign-out (`?signedOut=true`) ends the provider
 *   session too, so signing out actually signs out.
 * - Anything that keeps the form from loading falls back to the hosted page.
 */
@Component({
  selector: 'app-cloud-sign-in',
  standalone: true,
  imports: [ButtonComponent, LoadingIndicatorComponent],
  template: `
    @switch (state()) {
      @case ('loading') {
        <nostos-loading-indicator label="Loading sign-in" size="md" />
      }
      @case ('continuing') {
        <p class="cloud-sign-in__status">
          <nostos-loading-indicator decorative size="sm" />
          <span>Signing you in…</span>
        </p>
      }
      @case ('returning') {
        <p>
          Signed in as <strong>{{ returningEmail() ?? 'your account' }}</strong>.
        </p>
        <div class="cloud-sign-in__actions">
          <button appButton variant="primary" type="button" (click)="continueIn()">
            Continue
          </button>
          <button appButton variant="ghost" type="button" (click)="useAnotherAccount()">
            Use another account
          </button>
        </div>
      }
      @case ('hosted') {
        <button appButton variant="primary" type="button" (click)="signInHosted()">
          Sign in or create account
        </button>
      }
    }
    <div #form class="cloud-sign-in__form" [hidden]="state() !== 'form'"></div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 16px;
      width: 100%;
    }

    .cloud-sign-in__form {
      width: 100%;
    }

    .cloud-sign-in__status {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0;
      color: var(--color-text-muted);
    }

    p {
      margin: 0;
      color: var(--color-text-muted);
    }

    .cloud-sign-in__actions {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }

    @media (max-width: 600px) {
      :host > button,
      .cloud-sign-in__actions,
      .cloud-sign-in__actions button {
        width: 100%;
      }

      .cloud-sign-in__actions {
        flex-direction: column;
      }
    }
  `,
})
export class CloudSignInComponent implements OnInit {
  private readonly entry = inject(CloudEntryService);
  private readonly signIn = inject(CloudSignInService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly formNode = viewChild.required<ElementRef<HTMLElement>>('form');
  private clerk: ClerkSignInHost | null = null;
  private mounted = false;
  private destroyed = false;

  readonly state = signal<SignInState>('loading');
  readonly returningEmail = signal<string | null>(null);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.unmount();
    });
  }

  async ngOnInit(): Promise<void> {
    const options = await this.signIn.options();
    const clerk = options ? await this.signIn.loadClerk(options.publishableKey) : null;
    if (this.destroyed) return;
    if (!clerk) {
      this.state.set('hosted');
      return;
    }
    this.clerk = clerk;

    const fromSignOut = this.arrivedFromSignOut();
    if (fromSignOut) {
      if (clerk.user) {
        try {
          await clerk.signOut();
        } catch {
          // The Nostos cookie is already gone; a provider session that
          // survives here is offered as "Continue" rather than forced.
        }
      }
      this.forgetSignOutMarker();
      sessionStorage.removeItem(AUTO_CONTINUE_KEY);
    }
    if (this.destroyed) return;

    if (clerk.user) {
      this.returningEmail.set(clerk.user.primaryEmailAddress?.emailAddress ?? null);
      if (!fromSignOut && this.mayAutoContinue()) {
        this.continueIn();
      } else {
        this.state.set('returning');
      }
      return;
    }

    this.mountForm();
  }

  continueIn(): void {
    sessionStorage.setItem(AUTO_CONTINUE_KEY, String(Date.now()));
    this.state.set('continuing');
    this.navigateTo(this.entry.continueUrl());
  }

  async useAnotherAccount(): Promise<void> {
    if (!this.clerk) return;
    this.state.set('loading');
    try {
      await this.clerk.signOut();
    } catch {
      this.state.set('hosted');
      return;
    }
    this.returningEmail.set(null);
    this.mountForm();
  }

  signInHosted(): void {
    this.navigateTo(this.entry.loginUrl());
  }

  navigateTo(url: string): void {
    globalThis.location.assign(url);
  }

  private mountForm(): void {
    if (!this.clerk || this.destroyed) return;
    // After the form completes, Clerk navigates here with a full page load,
    // and the backend login finishes the code flow against the session the
    // form just created.
    const continueUrl = new URL(this.entry.continueUrl(), globalThis.location.origin).toString();
    this.unmount();
    this.clerk.mountSignIn(this.formNode().nativeElement, {
      routing: 'virtual',
      withSignUp: true,
      forceRedirectUrl: continueUrl,
      signUpForceRedirectUrl: continueUrl,
      appearance: this.appearance(),
    });
    this.mounted = true;
    this.state.set('form');
  }

  private unmount(): void {
    if (!this.mounted || !this.clerk) return;
    this.clerk.unmountSignIn(this.formNode().nativeElement);
    this.mounted = false;
  }

  private mayAutoContinue(): boolean {
    const last = Number(sessionStorage.getItem(AUTO_CONTINUE_KEY));
    return !Number.isFinite(last) || Date.now() - last > AUTO_CONTINUE_WINDOW_MS;
  }

  private arrivedFromSignOut(): boolean {
    return new URLSearchParams(globalThis.location.search).get('signedOut') === 'true';
  }

  private forgetSignOutMarker(): void {
    const url = new URL(globalThis.location.href);
    url.searchParams.delete('signedOut');
    history.replaceState(history.state, '', `${url.pathname}${url.search}${url.hash}`);
  }

  /**
   * The provider form drawn with the app's own tokens. Clerk v5 parses
   * colours itself, so the resolved values are passed rather than `var()`.
   */
  private appearance(): Record<string, unknown> {
    const style = getComputedStyle(this.host.nativeElement);
    const token = (name: string) => style.getPropertyValue(name).trim();
    const fontFamily = style.fontFamily;
    return {
      variables: {
        colorPrimary: token('--cta-surface'),
        colorTextOnPrimaryBackground: token('--cta-ink'),
        colorBackground: token('--bg-surface'),
        colorInputBackground: token('--bg-input'),
        colorText: token('--color-text-main'),
        colorTextSecondary: token('--color-text-muted'),
        colorInputText: token('--color-text-main'),
        colorDanger: token('--color-danger'),
        colorNeutral: token('--color-text-main'),
        fontFamily,
        fontFamilyButtons: fontFamily,
        fontSize: '1rem',
        borderRadius: token('--radius-surface') || '10px',
      },
      elements: {
        rootBox: { width: '100%' },
        // The card sits at margin -1px inside an overflow-hidden box, which
        // clips the first glyph of each label once the padding is gone.
        cardBox: {
          width: '100%',
          maxWidth: 'none',
          boxShadow: 'none',
          border: 'none',
          overflow: 'visible',
        },
        card: {
          margin: '0',
          padding: '0',
          boxShadow: 'none',
          border: 'none',
          background: 'transparent',
        },
        header: { display: 'none' },
        footer: { background: 'transparent', padding: '16px 0 0' },
        formButtonPrimary: { boxShadow: 'none', textTransform: 'none' },
      },
    };
  }
}
