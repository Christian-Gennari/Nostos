import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  ManagedBackupListing,
  ManagedBackupRestoreRequest,
  ManagedBackupRestoreResult,
} from '../dtos/managed-backups.dtos';

/** Reads and restores provider-neutral customer managed backups. */
@Injectable({ providedIn: 'root' })
export class ManagedBackupsService {
  private readonly http = inject(HttpClient);

  getBackups(): Observable<ManagedBackupListing> {
    return this.http.get<ManagedBackupListing>('/api/managed-backups');
  }

  restore(
    backupId: string,
    request: ManagedBackupRestoreRequest,
  ): Observable<ManagedBackupRestoreResult> {
    return this.http.post<ManagedBackupRestoreResult>(
      `/api/managed-backups/${encodeURIComponent(backupId)}/restore`,
      request,
    );
  }
}
