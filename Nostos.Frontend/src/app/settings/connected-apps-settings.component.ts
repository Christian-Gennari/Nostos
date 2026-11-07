import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';

import {
  McpAccessTokenCreated,
  McpAppScope,
  McpConnectedApp,
  McpConnectedAppsService,
} from '../core/services/mcp-connected-apps.service';
import { ToastService } from '../core/services/toast.service';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { NoticeComponent } from '../ui/notice/notice.component';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { SegmentedComponent, SegmentedOption } from '../ui/segmented/segmented.component';

/** The apps the setup snippets cover, in the order they are offered. */
export type McpClientId = 'claude-code' | 'cursor' | 'vscode' | 'claude-desktop';

/** What a snippet shows where no token was just made. */
export const TOKEN_PLACEHOLDER = '<your-token>';

const COPIED_FEEDBACK_MS = 2000;

/**
 * The setup for one app, ready to paste. `endpoint` is this server's `/mcp`
 * address as the browser sees it; `token` is the one just made, or the
 * placeholder.
 */
export function mcpClientSnippet(client: McpClientId, endpoint: string, token: string): string {
  const header = `Bearer ${token}`;
  switch (client) {
    case 'claude-code':
      return `claude mcp add --transport http nostos ${endpoint} --header "Authorization: ${header}"`;
    case 'cursor':
      return JSON.stringify(
        { mcpServers: { nostos: { url: endpoint, headers: { Authorization: header } } } },
        null,
        2,
      );
    case 'vscode':
      return JSON.stringify(
        { servers: { nostos: { type: 'http', url: endpoint, headers: { Authorization: header } } } },
        null,
        2,
      );
    case 'claude-desktop': {
      // mcp-remote refuses plain http except on this computer, so an address
      // on the home network needs its --allow-http. The header goes through
      // an environment variable because Claude Desktop on Windows splits
      // arguments at spaces.
      const local = /^http:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|\/)/.test(endpoint);
      const args = ['mcp-remote', endpoint, '--header', 'Authorization:${AUTH_HEADER}'];
      if (endpoint.startsWith('http://') && !local) args.push('--allow-http');
      return JSON.stringify(
        { mcpServers: { nostos: { command: 'npx', args, env: { AUTH_HEADER: header } } } },
        null,
        2,
      );
    }
  }
}

/** Where each snippet goes, said once under it. */
const CLIENT_HINTS: Record<McpClientId, string> = {
  'claude-code': 'Run this in a terminal. Claude Code can then use Nostos in every project.',
  cursor: 'Put this in ~/.cursor/mcp.json, or in .cursor/mcp.json in one project.',
  vscode: 'Put this in .vscode/mcp.json in a project, or run “MCP: Open User Configuration”.',
  'claude-desktop':
    'In Claude Desktop, open Settings → Developer → Edit Config and add this to claude_desktop_config.json. It needs Node.js.',
};

/**
 * Settings → Connected apps (issue #1469): the AI apps connected to this
 * library over MCP, on any deployment that keeps a record of them.
 *
 * Where the server can make access tokens (`canCreate`, SelfHosted): make one,
 * see it once, copy the setup for the app, revoke it later. The token is held
 * only in this component's memory until the notice is closed: it is never
 * stored in the browser and the server cannot show it again. Where apps sign
 * in by themselves (Cloud), the section lists them, can revoke them, and
 * gives the address to connect from.
 */
@Component({
  selector: 'app-connected-apps-settings',
  standalone: true,
  imports: [
    ButtonComponent,
    ConfirmModal,
    EmptyStateComponent,
    IconButtonComponent,
    InputDirective,
    LoadingIndicatorComponent,
    NostosDatePipe,
    NostosIconComponent,
    NoticeComponent,
    SegmentedComponent,
  ],
  templateUrl: './connected-apps-settings.component.html',
  styleUrl: './connected-apps-settings.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConnectedAppsSettingsComponent implements OnInit {
  private readonly api = inject(McpConnectedAppsService);
  private readonly toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  readonly tokens = signal<McpConnectedApp[] | null>(null);
  /** Apps that may connect. */
  readonly active = computed(() => this.tokens()?.filter((app) => !app.revoked) ?? null);
  /** Apps the person revoked that stay blocked until allowed again (#1483). */
  readonly blocked = computed(() => this.tokens()?.filter((app) => app.revoked) ?? []);
  readonly restoring = signal<string | null>(null);
  readonly loadFailed = signal(false);
  /** Whether this server makes tokens; false where apps sign in by themselves. */
  readonly canCreate = signal(false);

  readonly name = signal('');
  readonly scope = signal<McpAppScope>('read');
  readonly creating = signal(false);
  /** The token just made. Gone once the owner closes the notice. */
  readonly created = signal<McpAccessTokenCreated | null>(null);

  readonly pendingRevoke = signal<McpConnectedApp | null>(null);
  readonly revoking = signal(false);

  readonly client = signal<McpClientId>('claude-code');
  readonly copied = signal<'token' | 'snippet' | 'address' | null>(null);
  private copiedTimeout: ReturnType<typeof setTimeout> | null = null;

  readonly scopeOptions: readonly SegmentedOption<McpAppScope>[] = [
    { value: 'read', label: 'Read only' },
    { value: 'read_write', label: 'Read and write' },
  ];
  readonly clientOptions: readonly SegmentedOption<McpClientId>[] = [
    { value: 'claude-code', label: 'Claude Code' },
    { value: 'cursor', label: 'Cursor' },
    { value: 'vscode', label: 'VS Code' },
    { value: 'claude-desktop', label: 'Claude Desktop' },
  ];

  /** This server's MCP address, as this browser reaches it. */
  readonly endpoint = `${this.document.location?.origin ?? ''}/mcp`;
  readonly canSubmit = computed(() => this.name().trim().length > 0 && !this.creating());
  readonly snippet = computed(() =>
    mcpClientSnippet(this.client(), this.endpoint, this.created()?.token ?? TOKEN_PLACEHOLDER),
  );
  readonly placeholder = TOKEN_PLACEHOLDER;
  readonly clientHint = computed(() => CLIENT_HINTS[this.client()]);

  constructor() {
    this.destroyRef.onDestroy(() => {
      if (this.copiedTimeout !== null) clearTimeout(this.copiedTimeout);
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loadFailed.set(false);
    this.api.list().subscribe({
      next: (page) => {
        this.canCreate.set(page.canCreate);
        this.tokens.set(page.apps);
      },
      error: () => this.loadFailed.set(true),
    });
  }

  setName(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
  }

  create(): void {
    const name = this.name().trim();
    if (!name || this.creating() || !this.canCreate()) return;
    this.creating.set(true);
    this.api.createToken(name, this.scope()).subscribe({
      next: (created) => {
        this.creating.set(false);
        this.created.set(created);
        this.name.set('');
        this.tokens.update((tokens) => [created.details, ...(tokens ?? [])]);
      },
      error: (error: HttpErrorResponse) => {
        this.creating.set(false);
        this.toast.error(
          error.status === 409 ? 'This server has too many tokens. Revoke one first.' : 'Could not make the token',
        );
      },
    });
  }

  /** Closing the notice forgets the token: it cannot be shown again. */
  dismissCreated(): void {
    this.created.set(null);
    this.copied.set(null);
  }

  requestRevoke(token: McpConnectedApp): void {
    if (this.revoking()) return;
    this.pendingRevoke.set(token);
  }

  cancelRevoke(): void {
    if (this.revoking()) return;
    this.pendingRevoke.set(null);
  }

  confirmRevoke(): void {
    const token = this.pendingRevoke();
    if (!token || this.revoking()) return;
    this.revoking.set(true);
    this.api.revoke(token.id).subscribe({
      next: () => this.afterRevoke(token),
      error: (error: HttpErrorResponse) => {
        // Already gone is what was asked for.
        if (error.status === 404) {
          this.afterRevoke(token);
          return;
        }
        this.revoking.set(false);
        this.toast.error(`Could not disconnect ${token.name}`);
      },
    });
  }

  /** Lets a blocked app connect again. */
  restore(app: McpConnectedApp): void {
    if (this.restoring()) return;
    this.restoring.set(app.id);
    this.api.restore(app.id).subscribe({
      next: () => {
        this.restoring.set(null);
        this.tokens.update((tokens) => (tokens ?? []).map((t) => (t.id === app.id ? { ...t, revoked: false } : t)));
        this.toast.success(`${app.name} can connect again`);
      },
      error: () => {
        this.restoring.set(null);
        this.toast.error(`Could not allow ${app.name} again`);
      },
    });
  }

  private afterRevoke(token: McpConnectedApp): void {
    this.revoking.set(false);
    this.pendingRevoke.set(null);
    // Where the host keeps revoked apps (Cloud), they move to the blocked list.
    this.tokens.update((tokens) =>
      (tokens ?? []).flatMap((t) =>
        t.id !== token.id ? [t] : this.canCreate() ? [] : [{ ...t, revoked: true }],
      ),
    );
    if (this.created()?.details.id === token.id) this.dismissCreated();
    this.toast.success(`${token.name} can no longer connect`);
  }

  copyToken(): void {
    const token = this.created()?.token;
    if (token) this.copy(token, 'token', 'Token copied');
  }

  copySnippet(): void {
    this.copy(this.snippet(), 'snippet', 'Setup copied');
  }

  copyAddress(): void {
    this.copy(this.endpoint, 'address', 'Address copied');
  }

  scopeLabel(scope: McpAppScope): string {
    return scope === 'read_write' ? 'Read and write' : 'Read only';
  }

  /** The clipboard needs a secure context; the text on screen stays selectable either way. */
  private copy(text: string, what: 'token' | 'snippet' | 'address', done: string): void {
    const clipboard = this.document.defaultView?.navigator.clipboard;
    if (!clipboard) {
      this.toast.error('Copying is not available here — select the text and copy it.');
      return;
    }
    clipboard.writeText(text).then(
      () => {
        this.copied.set(what);
        this.toast.success(done);
        if (this.copiedTimeout !== null) clearTimeout(this.copiedTimeout);
        this.copiedTimeout = setTimeout(() => {
          this.copied.set(null);
          this.copiedTimeout = null;
        }, COPIED_FEEDBACK_MS);
      },
      () => this.toast.error('Could not copy automatically — select the text and copy it.'),
    );
  }
}
