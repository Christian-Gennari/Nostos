import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  AiProviderModelsRequest,
  AiProviderModelsResponse,
  AiProviderSettings,
  AiProviderTestRequest,
  AiProviderTestResult,
  AiProviderUpdate,
} from '../dtos/ai-provider.dtos';

/**
 * The four calls behind the settings page's "AI provider" card.
 *
 * The key is write-only from here: `get` reports whether one is usable and where
 * it comes from, and `update` sends one only when the user typed it or cleared
 * it. There is no read path for the key, by design.
 */
@Injectable({ providedIn: 'root' })
export class AiProviderService {
  private readonly http = inject(HttpClient);

  /** Effective settings for the available provider sections, plus key state. */
  get(): Observable<AiProviderSettings> {
    return this.http.get<AiProviderSettings>('/api/settings/ai-provider');
  }

  /** Sends only the provided sections; the response is the new effective state. */
  update(update: AiProviderUpdate): Observable<AiProviderSettings> {
    return this.http.put<AiProviderSettings>('/api/settings/ai-provider', update);
  }

  /**
   * Asks the endpoint what models it advertises. `baseUrl`/`apiKey` may carry
   * unsaved field values so the lookup matches what the user is about to save.
   */
  loadModels(request: AiProviderModelsRequest): Observable<AiProviderModelsResponse> {
    return this.http.post<AiProviderModelsResponse>(
      '/api/settings/ai-provider/models',
      request,
    );
  }

  /** One real round-trip against the provider. Always answers 200; branch on `ok`. */
  test(request: AiProviderTestRequest): Observable<AiProviderTestResult> {
    return this.http.post<AiProviderTestResult>(
      '/api/settings/ai-provider/test',
      request,
    );
  }
}
