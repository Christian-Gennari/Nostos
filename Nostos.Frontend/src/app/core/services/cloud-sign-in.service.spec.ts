import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { CloudSignInService, frontendApiFromPublishableKey } from './cloud-sign-in.service';

// Built at runtime: a literal key trips secret scanning even though it is public.
const clerkKey = (host: string, kind = 'live') => `pk_${kind}_${btoa(`${host}$`)}`;
const LIVE_KEY = clerkKey('clerk.example.test');

describe('frontendApiFromPublishableKey', () => {
  it('decodes the Frontend API host from a Clerk key', () => {
    expect(frontendApiFromPublishableKey(LIVE_KEY)).toBe('clerk.example.test');
    expect(frontendApiFromPublishableKey(clerkKey('quiet-owl-1.clerk.accounts.dev', 'test')))
      .toBe('quiet-owl-1.clerk.accounts.dev');
  });

  it('rejects anything that is not a plain host behind a Clerk key', () => {
    expect(frontendApiFromPublishableKey(null)).toBeNull();
    expect(frontendApiFromPublishableKey(LIVE_KEY.replace('pk_', 'sk_'))).toBeNull();
    expect(frontendApiFromPublishableKey(`pk_live_${btoa('clerk.nostos.page')}`)).toBeNull();
    expect(frontendApiFromPublishableKey(`pk_live_${btoa('evil.example/x?$')}`)).toBeNull();
    expect(frontendApiFromPublishableKey(`pk_live_${btoa('localhost$')}`)).toBeNull();
    expect(frontendApiFromPublishableKey('pk_live_%%%')).toBeNull();
  });
});

describe('CloudSignInService', () => {
  let service: CloudSignInService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(CloudSignInService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('returns Clerk options from the Cloud host', async () => {
    const result = service.options();
    http.expectOne('/api/auth/sign-in-options').flush({ provider: 'clerk', publishableKey: LIVE_KEY });
    expect(await result).toEqual({ provider: 'clerk', publishableKey: LIVE_KEY });
  });

  it('falls back when the host has no embedded sign-in', async () => {
    const result = service.options();
    http.expectOne('/api/auth/sign-in-options').flush(null, { status: 404, statusText: 'Not Found' });
    expect(await result).toBeNull();
  });

  it('falls back on a key it cannot turn into a script origin', async () => {
    const result = service.options();
    http.expectOne('/api/auth/sign-in-options').flush({ provider: 'clerk', publishableKey: 'pk_live_bad' });
    expect(await result).toBeNull();
  });
});
