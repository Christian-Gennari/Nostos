import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { CloudAccountDeletionService } from './cloud-account-deletion.service';

describe('CloudAccountDeletionService', () => {
  let service: CloudAccountDeletionService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(CloudAccountDeletionService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads deletion status from the authenticated same-origin Cloud route', async () => {
    const promise = firstValueFrom(service.getStatus());
    const request = http.expectOne('/api/cloud/account/deletion/');

    expect(request.request.method).toBe('GET');
    request.flush({
      state: 'GracePeriod',
      gracePeriodDays: 14,
      requestedAtUtc: '2026-10-01T12:00:00Z',
      eligibleAtUtc: '2026-10-15T12:00:00Z',
      completedAtUtc: null,
      canCancel: true,
      portableExportUrl: '/api/portability/export',
    });

    expect((await promise).eligibleAtUtc).toBe('2026-10-15T12:00:00Z');
  });

  it('requires explicit confirmation when requesting deletion', async () => {
    const promise = firstValueFrom(service.requestDeletion());
    const request = http.expectOne('/api/cloud/account/deletion/');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ confirm: true });
    request.flush({
      state: 'GracePeriod',
      gracePeriodDays: 14,
      requestedAtUtc: '2026-10-01T12:00:00Z',
      eligibleAtUtc: '2026-10-15T12:00:00Z',
      completedAtUtc: null,
      canCancel: true,
      portableExportUrl: '/api/portability/export',
    });
    await promise;
  });

  it('cancels without a request body during the grace period', async () => {
    const promise = firstValueFrom(service.cancelDeletion());
    const request = http.expectOne('/api/cloud/account/deletion/cancel');

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toBeNull();
    request.flush({
      state: 'Cancelled',
      gracePeriodDays: 14,
      requestedAtUtc: '2026-10-01T12:00:00Z',
      eligibleAtUtc: '2026-10-15T12:00:00Z',
      completedAtUtc: null,
      canCancel: false,
      portableExportUrl: null,
    });
    expect((await promise).state).toBe('Cancelled');
  });
});
