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
 * The apps connected to this library over MCP (issue #1469). Callers check
 * `supportsMcpConnectedApps` first. Listing and revoking work on every host
 * that has the feature; making a token only where `canCreate` is true
 * (SelfHosted). On Cloud apps connect themselves by signing in.
 */
@Injectable({ providedIn: 'root' })
export class McpConnectedAppsService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/mcp/apps';

  list(): Observable<McpConnectedApps> {
    return this.http.get<McpConnectedApps>(this.base);
  }

  createToken(name: string, scope: McpAppScope): Observable<McpAccessTokenCreated> {
    return this.http.post<McpAccessTokenCreated>('/api/mcp/tokens', { name, scope });
  }

  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }

  /** Lets a revoked app connect again (#1483). */
  restore(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${encodeURIComponent(id)}/restore`, {});
  }
}
