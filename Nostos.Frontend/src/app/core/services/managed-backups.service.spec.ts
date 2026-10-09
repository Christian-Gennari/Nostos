import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { ManagedBackupsService } from './managed-backups.service';

describe('ManagedBackupsService', () => {
  let service: ManagedBackupsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(ManagedBackupsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the stable public managed-backups route', async () => {
    const resultPromise = firstValueFrom(service.getBackups());
    const request = http.expectOne('/api/managed-backups');

    expect(request.request.method).toBe('GET');
    request.flush({
      retentionDays: 14,
      backups: [
        {
          id: 'backup-1',
          createdAtUtc: '2026-10-07T00:30:00Z',
          archiveBytes: 1_024,
          mediaBytes: 2_048,
          state: 'completed',
        },
      ],
    });

    const listing = await resultPromise;
    expect(listing.retentionDays).toBe(14);
    expect(listing.backups[0].state).toBe('completed');
  });

  it('posts explicit confirmation to the public restore endpoint', async () => {
    const id = '2a226c30-6378-4f9c-993a-f3fdd3078a71';
    const resultPromise = firstValueFrom(
      service.restore(id, { confirm: true }),
    );

    const request = http.expectOne(
      `/api/managed-backups/${id}/restore`,
    );

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ confirm: true });

    request.flush({
      backupId: id,
      restoredAtUtc: '2026-10-09T14:00:00Z',
    });

    expect((await resultPromise).backupId).toBe(id);
  });
});
