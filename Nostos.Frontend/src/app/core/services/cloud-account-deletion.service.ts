import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  CloudAccountDeletionRequest,
  CloudAccountDeletionStatus,
} from '../dtos/cloud-account-deletion.dtos';

/** Same-origin Cloud account deletion API. Authentication stays in the host cookie. */
@Injectable({ providedIn: 'root' })
export class CloudAccountDeletionService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/cloud/account/deletion/';

  getStatus(): Observable<CloudAccountDeletionStatus> {
    return this.http.get<CloudAccountDeletionStatus>(this.baseUrl);
  }

  requestDeletion(): Observable<CloudAccountDeletionStatus> {
    const request: CloudAccountDeletionRequest = { confirm: true };
    return this.http.post<CloudAccountDeletionStatus>(this.baseUrl, request);
  }

  cancelDeletion(): Observable<CloudAccountDeletionStatus> {
    return this.http.post<CloudAccountDeletionStatus>(`${this.baseUrl}cancel`, null);
  }
}
