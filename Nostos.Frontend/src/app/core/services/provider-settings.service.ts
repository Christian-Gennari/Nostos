import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  ProviderPreferenceUpdate,
  ProviderSettingsItem,
  ProviderSettingsResponse,
} from '../dtos/provider.dtos';

/**
 * The two calls behind the Settings "Book providers" card (issue #774).
 *
 * `list` is the only surface that includes disabled sources, so it is what the
 * management view renders; `setEnabled` stores one explicit choice and returns
 * the updated item, which is what the card re-seats in its list.
 */
@Injectable({ providedIn: 'root' })
export class ProviderSettingsService {
  private readonly http = inject(HttpClient);

  list(): Observable<ProviderSettingsResponse> {
    return this.http.get<ProviderSettingsResponse>('/api/settings/providers');
  }

  setEnabled(providerId: string, enabled: boolean): Observable<ProviderSettingsItem> {
    const update: ProviderPreferenceUpdate = { enabled };
    return this.http.put<ProviderSettingsItem>(
      `/api/settings/providers/${encodeURIComponent(providerId)}`,
      update,
    );
  }
}
