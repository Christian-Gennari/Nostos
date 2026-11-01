import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';

import { CloudAuthService } from './cloud-auth.service';

describe('CloudAuthService', () => {
  let service: CloudAuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(CloudAuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads safe session state from the BFF', async () => {
    const resultPromise = firstValueFrom(service.getSession());

    const request = http.expectOne('/api/auth/session');
    expect(request.request.method).toBe('GET');
    request.flush({
      authenticated: true,
      accountState: 'Active',
      account: {
        id: '1e4df713-1a34-4fc7-9a90-c45169256845',
        displayName: 'Reader',
        email: 'reader@example.test',
      },
    });

    const result = await resultPromise;
    expect(result.authenticated).toBe(true);
    expect(result.account?.displayName).toBe('Reader');
  });

  it('caches session state until an explicit refresh', async () => {
    const firstPromise = firstValueFrom(service.getSession());
    http.expectOne('/api/auth/session').flush({
      authenticated: false,
      accountState: null,
      account: null,
    });
    await firstPromise;

    await firstValueFrom(service.getSession());
    http.expectNone('/api/auth/session');

    const refreshPromise = firstValueFrom(service.getSession(true));
    http.expectOne('/api/auth/session').flush({
      authenticated: true,
      accountState: 'Active',
      account: {
        id: '1e4df713-1a34-4fc7-9a90-c45169256845',
        displayName: 'Reader',
        email: null,
      },
    });

    expect((await refreshPromise).authenticated).toBe(true);
  });

  it('submits logout as a top-level POST to the BFF', async () => {
    const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});

    await service.logout();

    expect(submit).toHaveBeenCalledTimes(1);
    const form = submit.mock.instances[0] as HTMLFormElement;
    expect(form.method.toLowerCase()).toBe('post');
    expect(form.getAttribute('action')).toBe('/api/auth/logout');
    expect(document.body.contains(form)).toBe(false);

    submit.mockRestore();
  });

  describe('the EPUB files this browser kept (#1316)', () => {
    const KEPT = 'nostos-epub-files-v1:account-a';
    let names: string[];

    beforeEach(() => {
      names = [KEPT, 'nostos-epub-files-v1:account-b', 'ngsw:db:control'];
      vi.stubGlobal('caches', {
        keys: async () => names,
        delete: async (name: string) => {
          names = names.filter((kept) => kept !== name);
          return true;
        },
      });
    });

    afterEach(() => vi.unstubAllGlobals());

    const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

    it('are gone before logout leaves the page', async () => {
      const left: string[][] = [];
      const submit = vi
        .spyOn(HTMLFormElement.prototype, 'submit')
        .mockImplementation(() => void left.push([...names]));

      await service.logout();

      expect(left).toEqual([['ngsw:db:control']]);
      submit.mockRestore();
    });

    it('do not hold up logout when clearing them stalls', async () => {
      vi.stubGlobal('caches', { keys: () => new Promise(() => {}) });
      const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
      vi.useFakeTimers();

      const leaving = service.logout();
      await vi.advanceTimersByTimeAsync(1000);
      await leaving;

      expect(submit).toHaveBeenCalledTimes(1);
      vi.useRealTimers();
      submit.mockRestore();
    });

    it('go when the session expires', async () => {
      service.invalidateSession();
      await settle();

      expect(names).toEqual(['ngsw:db:control']);
    });

    it('go when a session read finds nobody signed in', async () => {
      const result = firstValueFrom(service.getSession());
      http.expectOne('/api/auth/session').flush({ authenticated: false });
      await result;
      await settle();

      expect(names).toEqual(['ngsw:db:control']);
    });

    it('stay for the account that is signed in, and only for it', async () => {
      const result = firstValueFrom(service.getSession());
      http.expectOne('/api/auth/session').flush({ authenticated: true, account: { id: 'account-a' } });
      await result;
      await settle();

      expect(names).toEqual([KEPT, 'ngsw:db:control']);
    });
  });

  it('publishes only minimum session identity for host lifecycle callbacks', async () => {
    const changes: unknown[] = [];
    service.sessionChanges$.subscribe((session) => changes.push(session));
    const result = firstValueFrom(service.getSession());
    http.expectOne('/api/auth/session').flush({
      authenticated: true, accountState: 'Active',
      account: { id: 'account-a', displayName: 'Reader', email: 'reader@example.test' },
    });
    await result;
    expect(changes).toEqual([{ authenticated: true, accountId: 'account-a' }]);
  });

  it('invalidates before logout submission and ignores a delayed authenticated response', async () => {
    const changes: unknown[] = [];
    service.sessionChanges$.subscribe((session) => changes.push(session));
    const result = firstValueFrom(service.getSession());
    const request = http.expectOne('/api/auth/session');
    const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {
      expect(changes).toEqual([{ authenticated: false, accountId: null }]);
    });
    const leaving = service.logout();
    request.flush({ authenticated: true, account: { id: 'account-a' } });
    await leaving;
    await result;
    expect(changes).toEqual([{ authenticated: false, accountId: null }]);
    submit.mockRestore();
  });

  it('rejects an earlier account response after a newer session read', async () => {
    const changes: unknown[] = [];
    service.sessionChanges$.subscribe((session) => changes.push(session));
    const oldResult = firstValueFrom(service.getSession());
    const oldRequest = http.expectOne('/api/auth/session');
    const newResult = firstValueFrom(service.getSession(true));
    http.expectOne('/api/auth/session').flush({ authenticated: true, account: { id: 'account-b' } });
    await newResult;
    oldRequest.flush({ authenticated: true, account: { id: 'account-a' } });
    await oldResult;
    expect(changes).toEqual([{ authenticated: true, accountId: 'account-b' }]);
  });

  it('drops cached identity on expiry and fetches the next session', async () => {
    const result = firstValueFrom(service.getSession());
    http.expectOne('/api/auth/session').flush({ authenticated: true, account: { id: 'account-a' } });
    await result;
    service.invalidateSession();
    const changes: unknown[] = [];
    service.sessionChanges$.subscribe((session) => changes.push(session));
    expect(changes).toEqual([{ authenticated: false, accountId: null }]);
    const refresh = firstValueFrom(service.getSession());
    http.expectOne('/api/auth/session').flush({ authenticated: false, account: null });
    await refresh;
  });

  it('only creates login URLs with local return targets', () => {
    expect(service.loginUrl('/library')).toBe('/api/auth/login?returnUrl=%2Flibrary');
    expect(service.loginUrl('https://evil.example')).toBe('/api/auth/login?returnUrl=%2F');
    expect(service.loginUrl('//evil.example')).toBe('/api/auth/login?returnUrl=%2F');
    expect(service.loginUrl('/start', null, 'login')).toBe(
      '/api/auth/login?returnUrl=%2Fstart&prompt=login',
    );
  });

  it('preserves the selected Cloud offer inside a local auth return URL', () => {
    expect(service.loginUrl('/start', 'pro-annual')).toBe(
      '/api/auth/login?returnUrl=%2Fstart%3Foffer%3Dpro-annual',
    );
    expect(service.loginUrl('/start?source=pricing', 'standard-monthly')).toBe(
      '/api/auth/login?returnUrl=%2Fstart%3Fsource%3Dpricing%26offer%3Dstandard-monthly',
    );
    expect(service.loginUrl('/start?offer=standard-annual', 'pro-monthly')).toBe(
      '/api/auth/login?returnUrl=%2Fstart%3Foffer%3Dpro-monthly',
    );
  });

  it('never turns an offer into an external return target', () => {
    expect(service.loginUrl('https://evil.example', 'pro-annual')).toBe(
      '/api/auth/login?returnUrl=%2F',
    );
  });
});
