import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';

import { CloudSignInComponent } from './cloud-sign-in.component';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { ClerkSignInHost, CloudSignInService } from '../core/services/cloud-sign-in.service';

const KEY = `pk_live_${btoa('clerk.example.test$')}`;

describe('CloudSignInComponent', () => {
  let signIn: { options: ReturnType<typeof vi.fn>; loadClerk: ReturnType<typeof vi.fn> };
  let entry: { loginUrl: ReturnType<typeof vi.fn>; continueUrl: ReturnType<typeof vi.fn> };
  let clerk: {
    user: ClerkSignInHost['user'];
    load: ReturnType<typeof vi.fn>;
    mountSignIn: ReturnType<typeof vi.fn>;
    unmountSignIn: ReturnType<typeof vi.fn>;
    signOut: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    sessionStorage.clear();
    history.replaceState({}, '', '/library');
    clerk = {
      user: null,
      load: vi.fn(),
      mountSignIn: vi.fn(),
      unmountSignIn: vi.fn(),
      signOut: vi.fn(async () => {
        clerk.user = null;
      }),
    };
    signIn = {
      options: vi.fn().mockResolvedValue({ provider: 'clerk', publishableKey: KEY }),
      loadClerk: vi.fn().mockResolvedValue(clerk),
    };
    entry = {
      loginUrl: vi.fn().mockReturnValue('/api/auth/login?returnUrl=%2F&prompt=login'),
      continueUrl: vi.fn().mockReturnValue('/api/auth/login?returnUrl=%2Flibrary'),
    };
    TestBed.configureTestingModule({
      imports: [CloudSignInComponent],
      providers: [
        { provide: CloudSignInService, useValue: signIn },
        { provide: CloudEntryService, useValue: entry },
      ],
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(CloudSignInComponent);
    const navigate = vi.spyOn(fixture.componentInstance, 'navigateTo').mockImplementation(() => {});
    fixture.detectChanges(); // runs ngOnInit
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    return { fixture, navigate, el: fixture.nativeElement as HTMLElement };
  }

  it('mounts the provider form on the page, finishing through the backend login', async () => {
    const { fixture } = await render();

    expect(fixture.componentInstance.state()).toBe('form');
    expect(clerk.mountSignIn).toHaveBeenCalledTimes(1);
    const props = clerk.mountSignIn.mock.calls[0][1];
    expect(props.forceRedirectUrl).toBe(`${location.origin}/api/auth/login?returnUrl=%2Flibrary`);
    expect(props.signUpForceRedirectUrl).toBe(props.forceRedirectUrl);
    expect(props.withSignUp).toBe(true);
    expect(props.routing).toBe('virtual');

    fixture.destroy();
    expect(clerk.unmountSignIn).toHaveBeenCalled();
  });

  it('continues straight in when the provider session is still alive', async () => {
    clerk.user = { primaryEmailAddress: { emailAddress: 'reader@example.test' } };
    const { fixture, navigate } = await render();

    expect(fixture.componentInstance.state()).toBe('continuing');
    expect(navigate).toHaveBeenCalledWith('/api/auth/login?returnUrl=%2Flibrary');
    expect(clerk.mountSignIn).not.toHaveBeenCalled();
  });

  it('offers a button instead of looping when a hand-off just failed', async () => {
    clerk.user = { primaryEmailAddress: { emailAddress: 'reader@example.test' } };
    sessionStorage.setItem('nostos.cloud.signIn.autoContinuedAt', String(Date.now()));
    const { fixture, navigate, el } = await render();

    expect(fixture.componentInstance.state()).toBe('returning');
    expect(navigate).not.toHaveBeenCalled();
    expect(el.textContent).toContain('reader@example.test');

    await fixture.componentInstance.useAnotherAccount();
    expect(clerk.signOut).toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('form');
  });

  it('ends the provider session too after a Nostos sign-out', async () => {
    history.replaceState({}, '', '/start?signedOut=true');
    clerk.user = { primaryEmailAddress: { emailAddress: 'reader@example.test' } };
    const { fixture, navigate } = await render();

    expect(clerk.signOut).toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('form');
    expect(location.search).toBe('');
  });

  it('never signs straight back in after sign-out if the provider session survives', async () => {
    history.replaceState({}, '', '/start?signedOut=true');
    clerk.user = { primaryEmailAddress: { emailAddress: 'reader@example.test' } };
    clerk.signOut.mockRejectedValue(new Error('offline'));
    const { fixture, navigate } = await render();

    expect(navigate).not.toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('returning');
  });

  it('falls back to the hosted sign-in page when the form cannot load', async () => {
    signIn.loadClerk.mockResolvedValue(null);
    const { fixture, navigate, el } = await render();

    expect(fixture.componentInstance.state()).toBe('hosted');
    const button = el.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toContain('Sign in or create account');
    button.click();
    expect(navigate).toHaveBeenCalledWith('/api/auth/login?returnUrl=%2F&prompt=login');
  });

  it('falls back when the host offers no embedded sign-in', async () => {
    signIn.options.mockResolvedValue(null);
    const { fixture } = await render();

    expect(signIn.loadClerk).not.toHaveBeenCalled();
    expect(fixture.componentInstance.state()).toBe('hosted');
  });
});
