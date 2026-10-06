import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';

import { CloudAuthService } from './cloud-auth.service';
import { hostedSessionExpiryInterceptor } from './hosted-session-expiry.interceptor';

describe('hostedSessionExpiryInterceptor', () => {
  let client: HttpClient;
  let http: HttpTestingController;
  let invalidate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    invalidate = vi.fn();
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([hostedSessionExpiryInterceptor])),
      provideHttpClientTesting(),
      { provide: CloudAuthService, useValue: { invalidateSession: invalidate } },
    ] });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it.each(['/api/books', `${location.origin}/api/auth/session`])('invalidates on same-origin 401: %s', async (url) => {
    const result = firstValueFrom(client.get(url));
    const assertion = expect(result).rejects.toMatchObject({ status: 401 });
    http.expectOne(url).flush({}, { status: 401, statusText: 'Unauthorized' });
    await assertion;
    expect(invalidate).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['/api/books', 403], ['/api/books', 500],
    ['https://external.example.test/api/books', 401], ['/unrelated', 401], ['/api-like', 401],
  ])('preserves session for %s / %s', async (url, status) => {
    const result = firstValueFrom(client.get(url as string));
    const assertion = expect(result).rejects.toMatchObject({ status });
    http.expectOne(url as string).flush({}, { status: status as number, statusText: 'Failure' });
    await assertion;
    expect(invalidate).not.toHaveBeenCalled();
  });
});
