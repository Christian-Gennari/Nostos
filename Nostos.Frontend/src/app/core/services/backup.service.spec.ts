import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { BackupService } from './backup.service';

describe('BackupService', () => {
  let service: BackupService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(BackupService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the status, the settings and the progress', () => {
    service.getStatus().subscribe();
    const status = http.expectOne('/api/backup/status');
    expect(status.request.method).toBe('GET');
    status.flush({});

    service.getSettings().subscribe();
    const settings = http.expectOne('/api/backup/settings');
    expect(settings.request.method).toBe('GET');
    settings.flush({});

    service.getProgress().subscribe();
    const progress = http.expectOne('/api/backup/progress');
    expect(progress.request.method).toBe('GET');
    progress.flush({});
  });

  it('writes the settings', () => {
    const settings = { enabled: true, intervalHours: 24 };
    service.updateSettings(settings as never).subscribe();

    const request = http.expectOne('/api/backup/settings');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual(settings);
    request.flush({});
  });

  it('triggers a backup and restores one by id', () => {
    service.triggerBackup().subscribe();
    const trigger = http.expectOne('/api/backup/trigger');
    expect(trigger.request.method).toBe('POST');
    expect(trigger.request.body).toEqual({});
    trigger.flush({});

    service.restore('b1').subscribe();
    const restore = http.expectOne('/api/backup/restore/b1');
    expect(restore.request.method).toBe('POST');
    expect(restore.request.body).toEqual({});
    restore.flush({});
  });

  it('reads, deletes and imports history entries', () => {
    service.getHistory().subscribe();
    const history = http.expectOne('/api/backup/history');
    expect(history.request.method).toBe('GET');
    history.flush([]);

    service.deleteBackup('b1').subscribe();
    const removed = http.expectOne('/api/backup/history/b1');
    expect(removed.request.method).toBe('DELETE');
    removed.flush(null, { status: 204, statusText: 'No Content' });

    service.importExisting().subscribe();
    const imported = http.expectOne('/api/backup/import');
    expect(imported.request.method).toBe('POST');
    expect(imported.request.body).toEqual({});
    imported.flush([]);
  });

  it('builds the download URL without a request of its own', () => {
    expect(service.getDownloadUrl('backup 1')).toBe('/api/backup/download/backup 1');
    http.expectNone(() => true);
  });

  it('passes a refused restore through to the caller', () => {
    let status: number | undefined;
    service.restore('missing').subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/backup/restore/missing').flush(
      { message: 'No such backup' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(status).toBe(404);
  });
});
