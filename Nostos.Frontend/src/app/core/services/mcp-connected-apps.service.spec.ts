import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { McpConnectedAppsService } from './mcp-connected-apps.service';

describe('McpConnectedAppsService', () => {
  let service: McpConnectedAppsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(McpConnectedAppsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists the connected apps', () => {
    service.list().subscribe();

    const request = http.expectOne('/api/mcp/apps');
    expect(request.request.method).toBe('GET');
    request.flush({ canCreate: true, apps: [] });
  });

  it('makes a token for a named app and scope', () => {
    service.createToken('Claude Desktop', 'read_write').subscribe();

    const request = http.expectOne('/api/mcp/tokens');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ name: 'Claude Desktop', scope: 'read_write' });
    request.flush({ details: {}, token: 'secret' });
  });

  it('revokes and restores an app by id, escaping the id', () => {
    service.revoke('app 1').subscribe();
    const revoke = http.expectOne('/api/mcp/apps/app%201');
    expect(revoke.request.method).toBe('DELETE');
    revoke.flush(null, { status: 204, statusText: 'No Content' });

    service.restore('app 1').subscribe();
    const restore = http.expectOne('/api/mcp/apps/app%201/restore');
    expect(restore.request.method).toBe('POST');
    expect(restore.request.body).toEqual({});
    restore.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('reads the OAuth requests and answers them', () => {
    service.oauthStatus().subscribe();
    const status = http.expectOne('/api/mcp/oauth');
    expect(status.request.method).toBe('GET');
    status.flush({ enabled: true, connectorUrl: null, requests: [] });

    service.approveRequest('req 1', 'read').subscribe();
    const approve = http.expectOne('/api/mcp/oauth/requests/req%201/approve');
    expect(approve.request.method).toBe('POST');
    expect(approve.request.body).toEqual({ scope: 'read' });
    approve.flush(null, { status: 204, statusText: 'No Content' });

    service.denyRequest('req 1').subscribe();
    const deny = http.expectOne('/api/mcp/oauth/requests/req%201/deny');
    expect(deny.request.method).toBe('POST');
    expect(deny.request.body).toEqual({});
    deny.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('passes a host without the feature through as a 404', () => {
    let status: number | undefined;
    service.list().subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/mcp/apps').flush(
      { message: 'Not found' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(status).toBe(404);
  });
});
