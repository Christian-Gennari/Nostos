import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import { ManagedBackupListing } from '../dtos/managed-backups.dtos';

/** Reads the provider-neutral customer managed-backup list. */
@Injectable({ providedIn: 'root' })
export class ManagedBackupsService {
  private readonly http = inject(HttpClient);

  getBackups(): Observable<ManagedBackupListing> {
    return this.http.get<ManagedBackupListing>('/api/managed-backups');
  }
}
