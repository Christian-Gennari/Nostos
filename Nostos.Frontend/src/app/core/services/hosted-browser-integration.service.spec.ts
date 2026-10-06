import { TestBed } from '@angular/core/testing';
import { ReplaySubject, of, throwError } from 'rxjs';

import { HostedBrowserSession } from '../dtos/hosted-browser-integration.dtos';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { CloudAuthService } from './cloud-auth.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { HostedBrowserIntegrationService } from './hosted-browser-integration.service';

const route = '/api/runtime/hosted-browser-integration.js';
const accountA = { authenticated: true, accountId: 'account-a' };
const accountB = { authenticated: true, accountId: 'account-b' };
const anonymous = { authenticated: false, accountId: null };

describe('HostedBrowserIntegrationService', () => {
  let sessions: ReplaySubject<HostedBrowserSession>;
  let get: ReturnType<typeof vi.fn>;
  let service: HostedBrowserIntegrationService;

  beforeEach(() => {
    sessions = new ReplaySubject<HostedBrowserSession>(1);
    get = vi.fn().mockReturnValue(of({
      deploymentMode: 'Cloud', hostedBrowserIntegrationEnabled: true,
    } as DeploymentCapabilities));
    TestBed.configureTestingModule({ providers: [
      { provide: CloudAuthService, useValue: { sessionChanges$: sessions.asObservable() } },
      { provide: DeploymentCapabilitiesService, useValue: { get } },
    ] });
    service = TestBed.inject(HostedBrowserIntegrationService);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    document.querySelectorAll(`script[src="${route}"]`).forEach((script) => script.remove());
    delete window.nostosHostedBrowserIntegration;
  });

  function script(): HTMLScriptElement {
    return document.querySelector(`script[src="${route}"]`)!;
  }

  function loaded(callback = vi.fn()): ReturnType<typeof vi.fn> {
    window.nostosHostedBrowserIntegration = { sessionChanged: callback };
    script().dispatchEvent(new Event('load'));
    return callback;
  }

  it.each([
    { deploymentMode: 'SelfHosted', hostedBrowserIntegrationEnabled: true },
    { deploymentMode: 'Cloud', hostedBrowserIntegrationEnabled: false },
    { deploymentMode: 'Cloud' },
  ])('makes no host request for $deploymentMode / $hostedBrowserIntegrationEnabled', (capabilities) => {
    get.mockReturnValue(of(capabilities));
    service.start();
    expect(script()).toBeNull();
    expect(sessions.observed).toBe(false);
  });

  it('installs one asynchronous same-origin script across repeated startup/navigation', () => {
    service.start();
    service.start();
    sessions.next(accountA);
    loaded();
    service.start();
    expect(document.querySelectorAll(`script[src="${route}"]`).length).toBe(1);
    expect(script().async).toBe(true);
    expect(new URL(script().src).origin).toBe(location.origin);
    expect(get).toHaveBeenCalledTimes(1);
  });

  it('replays only the latest identity when auth arrives before the host script', () => {
    sessions.next(accountA);
    service.start();
    sessions.next(accountB);
    const callback = loaded();
    expect(callback.mock.calls).toEqual([[accountB]]);
  });

  it('delivers authentication arriving after the host script loads', () => {
    service.start();
    const callback = loaded();
    expect(callback).not.toHaveBeenCalled();
    sessions.next(accountA);
    sessions.next({ ...accountA });
    expect(callback.mock.calls).toEqual([[accountA]]);
  });

  it('delivers context replacement and session end without reinstalling', () => {
    service.start();
    const callback = loaded();
    sessions.next(accountA);
    sessions.next(accountB);
    sessions.next(anonymous);
    expect(callback.mock.calls).toEqual([[accountA], [accountB], [anonymous]]);
    expect(document.querySelectorAll(`script[src="${route}"]`).length).toBe(1);
  });

  it('does not replay a signed-out account after delayed script readiness', () => {
    service.start();
    sessions.next(accountA);
    sessions.next(anonymous);
    expect(loaded().mock.calls).toEqual([[anonymous]]);
  });

  it('does not let host code mutate replayed session state', () => {
    service.start();
    loaded(vi.fn((session: HostedBrowserSession) => { session.accountId = 'changed'; }));
    sessions.next(accountA);
    expect(accountA.accountId).toBe('account-a');
  });

  it('isolates capability, script and callback failures', () => {
    get.mockReturnValue(throwError(() => new Error('capabilities unavailable')));
    expect(() => service.start()).not.toThrow();
    expect(script()).toBeNull();
  });

  it('leaves failed or missing host code optional, without repeated installation', () => {
    service.start();
    script().dispatchEvent(new Event('error'));
    sessions.next(accountA);
    script().dispatchEvent(new Event('load')); // Missing registration also stays optional.
    service.start();
    expect(document.querySelectorAll(`script[src="${route}"]`).length).toBe(1);
  });

  it('isolates a thrown host callback from later authentication and sign-out', () => {
    service.start();
    const callback = loaded(vi.fn(() => { throw new Error('host failure'); }));
    expect(() => sessions.next(accountA)).not.toThrow();
    expect(() => sessions.next(anonymous)).not.toThrow();
    expect(callback).toHaveBeenCalledTimes(2);
  });
});
