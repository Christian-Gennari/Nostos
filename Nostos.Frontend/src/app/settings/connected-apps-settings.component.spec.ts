import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import {
  McpAccessTokenCreated,
  McpConnectedApp,
  McpConnectedAppsService,
  McpOAuthPendingRequest,
  McpOAuthStatus,
} from '../core/services/mcp-connected-apps.service';
import { ToastService } from '../core/services/toast.service';
import {
  ConnectedAppsSettingsComponent,
  OAUTH_POLL_MS,
  TOKEN_PLACEHOLDER,
  TUNNEL_GUIDE_URL,
  mcpClientSnippet,
} from './connected-apps-settings.component';

describe('ConnectedAppsSettingsComponent', () => {
  let fixture: ComponentFixture<ConnectedAppsSettingsComponent>;
  let component: ConnectedAppsSettingsComponent;
  let tokens: McpConnectedApp[];
  let canCreate: boolean;
  let oauth: McpOAuthStatus;

  const claudeRequest: McpOAuthPendingRequest = {
    id: 'r1',
    code: 'KMRT',
    clientName: 'Claude',
    clientId: 'https://claude.ai/oauth/mcp-oauth-client-metadata',
    clientHost: 'claude.ai',
    redirectHost: 'claude.ai',
    requestedScope: 'read_write',
    createdAt: '2026-11-07T10:00:00Z',
    expiresAt: '2026-11-07T10:10:00Z',
  };

  const existing: McpConnectedApp = {
    id: 'a1',
    name: 'Cursor',
    kind: 'token',
    scope: 'read',
    createdAt: '2026-11-01T09:00:00Z',
    lastUsedAt: null,
  };
  const secret = 'nst_' + 'x'.repeat(43);
  const made: McpAccessTokenCreated = {
    details: { id: 'b2', name: 'Claude Code', kind: 'token', scope: 'read_write', createdAt: '2026-11-07T10:00:00Z', lastUsedAt: null },
    token: secret,
  };

  const api = {
    list: vi.fn(() => of({ canCreate, apps: tokens })),
    createToken: vi.fn(() => of(made)),
    revoke: vi.fn(() => of(undefined as void)),
    oauthStatus: vi.fn(() => of(oauth)),
    approveRequest: vi.fn(() => of(undefined as void)),
    denyRequest: vi.fn(() => of(undefined as void)),
    restore: vi.fn(() => of(undefined as void)),
  };
  const toast = { success: vi.fn(), error: vi.fn() };

  const el = (testId: string) =>
    fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null;
  const all = (testId: string) =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`)) as HTMLElement[];
  const text = (testId: string) => el(testId)?.textContent?.replace(/\s+/g, ' ').trim();
  const typeName = (value: string) => {
    const input = el('connected-apps-name') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    tokens = [existing];
    canCreate = true;
    oauth = { enabled: false, connectorUrl: null, requests: [] };

    await TestBed.configureTestingModule({
      imports: [ConnectedAppsSettingsComponent],
      providers: [
        { provide: McpConnectedAppsService, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ConnectedAppsSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  });

  afterEach(() => fixture.destroy());

  it('lists tokens with their access and when they were used', () => {
    const items = all('connected-apps-item');
    expect(items).toHaveLength(1);
    expect(items[0].textContent).toContain('Cursor');
    expect(items[0].textContent).toContain('Read only');
    expect(items[0].textContent).toContain('not used yet');
  });

  it('says so when there are no tokens', async () => {
    tokens = [];
    component.load();
    await settle();

    expect(text('connected-apps-empty')).toContain('No tokens yet');
  });

  it('needs a name before it makes a token', () => {
    expect((el('connected-apps-make') as HTMLButtonElement).disabled).toBe(true);
    typeName('   ');
    expect((el('connected-apps-make') as HTMLButtonElement).disabled).toBe(true);
    typeName('Claude Code');
    expect((el('connected-apps-make') as HTMLButtonElement).disabled).toBe(false);
  });

  it('makes a token with the chosen access, shows it once and puts it in the setup', async () => {
    typeName('  Claude Code ');
    component.scope.set('read_write');
    el('connected-apps-make')!.click();
    await settle();

    expect(api.createToken).toHaveBeenCalledWith('Claude Code', 'read_write');
    expect(text('connected-apps-token-value')).toBe(secret);
    expect(text('connected-apps-snippet')).toContain(`Authorization: Bearer ${secret}`);
    expect(all('connected-apps-item')).toHaveLength(2);
    expect(all('connected-apps-item')[0].textContent).toContain('Read and write');
    expect((el('connected-apps-name') as HTMLInputElement).value).toBe('');

    el('connected-apps-done')!.click();
    await settle();

    expect(el('connected-apps-token-value')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain(secret);
    expect(text('connected-apps-snippet')).toContain(TOKEN_PLACEHOLDER);
  });

  it('copies the token', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    typeName('Claude Code');
    el('connected-apps-make')!.click();
    await settle();

    el('connected-apps-copy-token')!.click();
    await settle();

    expect(writeText).toHaveBeenCalledWith(secret);
    expect(text('connected-apps-copy-token')).toBe('Copied');
  });

  it('says when a token could not be made', async () => {
    api.createToken.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    typeName('Claude Code');
    el('connected-apps-make')!.click();
    await settle();

    expect(toast.error).toHaveBeenCalledWith('Could not make the token');
    expect(el('connected-apps-token-value')).toBeNull();
  });

  it('asks before it revokes, then removes the token', async () => {
    el('connected-apps-revoke')!.click();
    await settle();
    expect(api.revoke).not.toHaveBeenCalled();
    expect(component.pendingRevoke()).toEqual(existing);

    component.confirmRevoke();
    await settle();

    expect(api.revoke).toHaveBeenCalledWith('a1');
    expect(all('connected-apps-item')).toHaveLength(0);
    expect(component.pendingRevoke()).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('Cursor can no longer connect');
  });

  it('keeps a token whose revoke failed', async () => {
    api.revoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.requestRevoke(existing);
    component.confirmRevoke();
    await settle();

    expect(all('connected-apps-item')).toHaveLength(1);
    expect(toast.error).toHaveBeenCalledWith('Could not disconnect Cursor');
  });

  it('offers a retry when the list does not load', async () => {
    api.list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.load();
    await settle();

    expect(fixture.nativeElement.textContent).toContain('Could not load your connected apps');
  });

  it('lists signed-in apps where tokens cannot be made, with the address to connect from', async () => {
    canCreate = false;
    tokens = [
      { id: 'client_abc', name: 'Claude', kind: 'oauth_app', scope: 'read_write', createdAt: '2026-11-07T10:00:00Z', lastUsedAt: '2026-11-07T11:00:00Z' },
    ];
    component.load();
    await settle();

    expect(el('connected-apps-create')).toBeNull();
    expect(el('connected-apps-setup')).toBeNull();
    expect(text('connected-apps-endpoint')).toBe(component.endpoint);
    expect(component.endpoint.endsWith('/mcp')).toBe(true);
    const item = all('connected-apps-item')[0].textContent!;
    expect(item).toContain('Claude');
    expect(item).toContain('Read and write');
    expect(item).toContain('connected');
    expect(item).toContain('last used');

    component.create();
    expect(api.createToken).not.toHaveBeenCalled();

    el('connected-apps-revoke')!.click();
    component.confirmRevoke();
    await settle();
    expect(api.revoke).toHaveBeenCalledWith('client_abc');
  });

  it('keeps a revoked app blocked where the host keeps it, and allows it again (#1483)', async () => {
    canCreate = false;
    tokens = [
      { id: 'client_abc', name: 'Claude', kind: 'oauth_app', scope: 'read_write', createdAt: '2026-11-07T10:00:00Z', lastUsedAt: null },
      { id: 'client_old', name: 'ChatGPT', kind: 'oauth_app', scope: 'read', createdAt: '2026-11-01T10:00:00Z', lastUsedAt: null, revoked: true },
    ];
    component.load();
    await settle();

    expect(all('connected-apps-item').map((item) => item.textContent)).toEqual([expect.stringContaining('Claude')]);
    expect(text('connected-apps-blocked')).toContain('ChatGPT');
    expect(text('connected-apps-blocked')).toContain('Blocked');

    el('connected-apps-revoke')!.click();
    component.confirmRevoke();
    await settle();
    expect(all('connected-apps-blocked-item').map((item) => item.textContent)).toEqual([
      expect.stringContaining('Claude'),
      expect.stringContaining('ChatGPT'),
    ]);

    all('connected-apps-restore')[1].click();
    await settle();
    expect(api.restore).toHaveBeenCalledWith('client_old');
    expect(all('connected-apps-item').map((item) => item.textContent)).toEqual([expect.stringContaining('ChatGPT')]);
    expect(toast.success).toHaveBeenCalledWith('ChatGPT can connect again');
  });

  it('removes a revoked token where tokens are made, with nothing to allow again', async () => {
    el('connected-apps-revoke')!.click();
    component.confirmRevoke();
    await settle();

    expect(el('connected-apps-blocked')).toBeNull();
    expect(all('connected-apps-item')).toHaveLength(0);
  });

  it('says no app is connected yet where tokens cannot be made', async () => {
    canCreate = false;
    tokens = [];
    component.load();
    await settle();

    expect(text('connected-apps-empty')).toBe('No apps connected yet.');
  });

  it('switches the setup between apps', async () => {
    component.client.set('cursor');
    await settle();
    expect(JSON.parse(text('connected-apps-snippet')!)).toEqual({
      mcpServers: { nostos: { url: component.endpoint, headers: { Authorization: `Bearer ${TOKEN_PLACEHOLDER}` } } },
    });
  });

  describe('hosted apps over OAuth (SelfHosted, #1470)', () => {
    const enable = async (requests: McpOAuthPendingRequest[] = [claudeRequest]) => {
      oauth = { enabled: true, connectorUrl: 'https://nostos.example.ts.net/mcp', requests };
      component.loadOAuth();
      await settle();
    };

    it('points to the tunnel guide while the server has no public address', () => {
      expect(el('connected-apps-requests')).toBeNull();
      expect(el('connected-apps-connector-url')).toBeNull();
      const web = el('connected-apps-web')!;
      expect(web.textContent).toContain('Mcp__PublicBaseUrl');
      expect(web.querySelector('a')!.getAttribute('href')).toBe(TUNNEL_GUIDE_URL);
    });

    it('gives the connector address once OAuth is on', async () => {
      await enable([]);

      expect(text('connected-apps-connector-url')).toBe('https://nostos.example.ts.net/mcp');
      expect(el('connected-apps-requests')).toBeNull();
    });

    it('shows a waiting request with its code, the app and what it asks for', async () => {
      await enable();

      const request = text('connected-apps-request')!;
      expect(request).toContain('Claude wants to connect');
      expect(request).toContain('From claude.ai');
      expect(request).not.toContain('returns to');
      expect(request).toContain('asks to read and write');
      expect(text('connected-apps-request-code')).toBe('KMRT');
      expect(component.scopeFor(claudeRequest)).toBe('read_write');
    });

    it('names the return host when it differs from the app', async () => {
      await enable([{ ...claudeRequest, clientHost: 'apps.example', redirectHost: 'callback.example' }]);

      expect(text('connected-apps-request')).toContain('returns to callback.example');
    });

    it('approves with the access chosen, not only the one asked for', async () => {
      await enable();
      component.setRequestScope(claudeRequest, 'read');
      el('connected-apps-approve')!.click();
      await settle();

      expect(api.approveRequest).toHaveBeenCalledWith('r1', 'read');
      expect(el('connected-apps-request')).toBeNull();
      expect(toast.success).toHaveBeenCalledWith('Claude can connect. Go back to the app.');
    });

    it('approves with the requested access by default', async () => {
      await enable([{ ...claudeRequest, requestedScope: 'read' }]);
      el('connected-apps-approve')!.click();
      await settle();

      expect(api.approveRequest).toHaveBeenCalledWith('r1', 'read');
    });

    it('denies a request', async () => {
      await enable();
      el('connected-apps-deny')!.click();
      await settle();

      expect(api.denyRequest).toHaveBeenCalledWith('r1');
      expect(api.approveRequest).not.toHaveBeenCalled();
      expect(el('connected-apps-request')).toBeNull();
    });

    it('drops a request that ended before it was approved', async () => {
      await enable();
      api.approveRequest.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 404 })));
      el('connected-apps-approve')!.click();
      await settle();

      expect(el('connected-apps-request')).toBeNull();
      expect(toast.error).toHaveBeenCalledWith('That request has ended. Start again from the app.');
    });

    it('keeps a request whose approval failed', async () => {
      await enable();
      api.approveRequest.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
      el('connected-apps-approve')!.click();
      await settle();

      expect(el('connected-apps-request')).not.toBeNull();
      expect(component.deciding()).toBeNull();
      expect(toast.error).toHaveBeenCalledWith('Could not approve Claude');
    });

    it('looks for new requests while it is open, and stops when it closes', async () => {
      vi.useFakeTimers();
      try {
        oauth = { enabled: true, connectorUrl: 'https://nostos.example.ts.net/mcp', requests: [] };
        component.loadOAuth();
        const calls = api.oauthStatus.mock.calls.length;

        vi.advanceTimersByTime(OAUTH_POLL_MS);
        expect(api.oauthStatus.mock.calls.length).toBe(calls + 1);

        fixture.destroy();
        vi.advanceTimersByTime(OAUTH_POLL_MS * 3);
        expect(api.oauthStatus.mock.calls.length).toBe(calls + 1);
      } finally {
        vi.useRealTimers();
      }
    });

    it('does not ask about OAuth where apps sign in by themselves', async () => {
      api.oauthStatus.mockClear();
      canCreate = false;
      tokens = [];
      component.load();
      await settle();

      expect(api.oauthStatus).not.toHaveBeenCalled();
      expect(el('connected-apps-web')).toBeNull();
    });
  });
});

describe('mcpClientSnippet', () => {
  const token = 'nst_abc';

  it('gives Claude Code one command', () => {
    expect(mcpClientSnippet('claude-code', 'http://nas.local:5214/mcp', token)).toBe(
      'claude mcp add --transport http nostos http://nas.local:5214/mcp --header "Authorization: Bearer nst_abc"',
    );
  });

  it('gives VS Code its own servers shape', () => {
    expect(JSON.parse(mcpClientSnippet('vscode', 'https://n.example/mcp', token))).toEqual({
      servers: { nostos: { type: 'http', url: 'https://n.example/mcp', headers: { Authorization: 'Bearer nst_abc' } } },
    });
  });

  it('bridges Claude Desktop through mcp-remote, allowing http only where it must', () => {
    const lan = JSON.parse(mcpClientSnippet('claude-desktop', 'http://nas.local:5214/mcp', token));
    const local = JSON.parse(mcpClientSnippet('claude-desktop', 'http://localhost:5214/mcp', token));
    const tls = JSON.parse(mcpClientSnippet('claude-desktop', 'https://n.example/mcp', token));

    expect(lan.mcpServers.nostos).toEqual({
      command: 'npx',
      args: ['mcp-remote', 'http://nas.local:5214/mcp', '--header', 'Authorization:${AUTH_HEADER}', '--allow-http'],
      env: { AUTH_HEADER: 'Bearer nst_abc' },
    });
    expect(local.mcpServers.nostos.args).not.toContain('--allow-http');
    expect(tls.mcpServers.nostos.args).not.toContain('--allow-http');
  });
});
