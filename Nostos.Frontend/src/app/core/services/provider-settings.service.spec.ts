import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ProviderSettingsService } from './provider-settings.service';
import { ProviderSettingsItem, ProviderSettingsResponse } from '../dtos/provider.dtos';

/**
 * The two routes behind the Settings "Book providers" card (issue #774): a list
 * that includes disabled sources, and one explicit enable/disable write.
 */
describe('ProviderSettingsService', () => {
  let service: ProviderSettingsService;
  let http: HttpTestingController;

  const gutenberg: ProviderSettingsItem = {
    id: 'gutenberg',
    displayName: 'Project Gutenberg',
    description: 'Public-domain ebooks in many languages.',
    capabilities: ['search', 'ebookacquisition'],
    rightsNotice: 'Public domain in the USA (Project Gutenberg)',
    enabled: true,
    enabledByDefault: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(ProviderSettingsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the management list from the settings route', () => {
    let received: ProviderSettingsResponse | undefined;
    service.list().subscribe((response) => (received = response));

    const request = http.expectOne('/api/settings/providers');
    expect(request.request.method).toBe('GET');
    request.flush({ providers: [gutenberg] });

    expect(received).toEqual({ providers: [gutenberg] });
  });

  it('writes one explicit choice with a real boolean and no value coercion', () => {
    let updated: ProviderSettingsItem | undefined;
    service.setEnabled('wikisource', false).subscribe((item) => (updated = item));

    const request = http.expectOne('/api/settings/providers/wikisource');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ enabled: false });
    // The response is the server's item; the service never fabricates one.
    request.flush({ ...gutenberg, id: 'wikisource', enabled: false });

    expect(updated!.id).toBe('wikisource');
    expect(updated!.enabled).toBe(false);
  });

  it('escapes the provider id in the write URL', () => {
    service.setEnabled('a b/c', true).subscribe();

    const request = http.expectOne('/api/settings/providers/a%20b%2Fc');
    expect(request.request.method).toBe('PUT');
    request.flush(gutenberg);
  });
});
