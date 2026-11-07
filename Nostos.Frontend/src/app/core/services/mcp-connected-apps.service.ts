import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** `read`: search and quote only. `read_write`: also save notes, drafts and books to read. */
export type McpAppScope = 'read' | 'read_write';

/** `token`: an access token the owner made. `oauth_app`: an app that signed in. */
export type McpAppKind = 'token' | 'oauth_app';

/** One app connected over MCP. Never carries a token. */
export interface McpConnectedApp {
  id: string;
  name: string;
  kind: McpAppKind;
  scope: McpAppScope;
  createdAt: string;
  lastUsedAt: string | null;
  /** Revoked and blocked until allowed again (#1483). Only some hosts list these. */
  revoked?: boolean;
}

/** Settings' list: the apps, and whether this server can make a token. */
export interface McpConnectedApps {
  canCreate: boolean;
  apps: McpConnectedApp[];
}

/** The answer to making a token: the only time `token` is ever sent. */
export interface McpAccessTokenCreated {
  details: McpConnectedApp;
  token: string;
}

/**
 * A hosted AI app (claude.ai, ChatGPT) asking to connect through the public
 * address, waiting for the owner (issue #1470). `code` is the one the app's
 * sign-in page shows, so the owner approves the request they started.
 */
export interface McpOAuthPendingRequest {
  id: string;
  code: string;
  clientName: string;
  clientId: string;
  clientHost: string;
  redirectHost: string;
  requestedScope: McpAppScope;
  createdAt: string;
  expiresAt: string;
}

/** SelfHosted OAuth: on only with a public address; the connector URL to paste into the app. */
export interface McpOAuthStatus {
  enabled: boolean;
  connectorUrl: string | null;
  requests: McpOAuthPendingRequest[];
}

/**
 * The apps connected to this library over MCP (issue #1469). Callers check
 * `supportsMcpConnectedApps` first. Listing and revoking work on every host
 * that has the feature; making a token only where `canCreate` is true
 * (SelfHosted). On Cloud apps connect themselves by signing in.
 */
@Injectable({ providedIn: 'root' })
export class McpConnectedAppsService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/mcp/apps';
  private readonly oauthBase = '/api/mcp/oauth';

  list(): Observable<McpConnectedApps> {
    return this.http.get<McpConnectedApps>(this.base);
  }

  createToken(name: string, scope: McpAppScope): Observable<McpAccessTokenCreated> {
    return this.http.post<McpAccessTokenCreated>('/api/mcp/tokens', { name, scope });
  }

  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }

  /** SelfHosted only, and only on the private network: the server answers 404 on its public address. */
  oauthStatus(): Observable<McpOAuthStatus> {
    return this.http.get<McpOAuthStatus>(this.oauthBase);
  }

  approveRequest(id: string, scope: McpAppScope): Observable<void> {
    return this.http.post<void>(`${this.oauthBase}/requests/${encodeURIComponent(id)}/approve`, { scope });
  }

  denyRequest(id: string): Observable<void> {
    return this.http.post<void>(`${this.oauthBase}/requests/${encodeURIComponent(id)}/deny`, {});
  }

  /** Lets a revoked app connect again (#1483). */
  restore(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${encodeURIComponent(id)}/restore`, {});
  }
}
