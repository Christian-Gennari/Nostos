import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, ReplaySubject, shareReplay, tap } from 'rxjs';
import { HostedBrowserSession } from '../dtos/hosted-browser-integration.dtos';

import { CloudSession } from '../dtos/cloud-auth.dtos';

/**
 * Safe browser-facing Cloud session state.
 *
 * Tokens, OIDC issuer/subject claims and provider credentials never enter the
 * Angular application. Interactive sign-in is a top-level navigation to the
 * backend BFF endpoint; the browser then receives only the secure session
 * cookie managed by ASP.NET Core.
 */
@Injectable({ providedIn: 'root' })
export class CloudAuthService {
  private readonly http = inject(HttpClient);
  private session$?: Observable<CloudSession>;
  private sessionRevision = 0;
  private readonly sessionChanges = new ReplaySubject<HostedBrowserSession>(1);
  readonly sessionChanges$ = this.sessionChanges.asObservable();

  getSession(refresh = false): Observable<CloudSession> {
    if (refresh || !this.session$) {
      const revision = ++this.sessionRevision;
      this.session$ = this.http
        .get<CloudSession>('/api/auth/session')
        .pipe(
          tap((session) => {
            // A slow earlier read must not restore context after logout,
            // expiry, or a newer account/session read.
            if (revision !== this.sessionRevision) return;
            const accountId = session.authenticated ? session.account?.id ?? null : null;
            this.sessionChanges.next({ authenticated: accountId !== null, accountId });
          }),
          shareReplay({ bufferSize: 1, refCount: false }),
        );
    }

    return this.session$;
  }

  loginUrl(returnUrl: string, offerId: string | null = null, prompt: string | null = null): string {
    const isLocalReturnUrl = returnUrl.startsWith('/') && !returnUrl.startsWith('//');
    const safeReturnUrl = isLocalReturnUrl ? returnUrl : '/';
    const target =
      isLocalReturnUrl && offerId !== null
        ? this.withOffer(safeReturnUrl, offerId)
        : safeReturnUrl;

    const query = new URLSearchParams();
    query.set('returnUrl', target);
    if (prompt) {
      query.set('prompt', prompt);
    }

    return `/api/auth/login?${query.toString()}`;
  }

  invalidateSession(): void {
    ++this.sessionRevision;
    this.session$ = undefined;
    this.sessionChanges.next({ authenticated: false, accountId: null });
  }

  logout(): void {
    this.invalidateSession();
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = '/api/auth/logout';
    form.hidden = true;
    document.body.appendChild(form);
    form.submit();
    form.remove();
  }

  private withOffer(returnUrl: string, offerId: string): string {
    const target = new URL(returnUrl, 'https://nostos.local');
    target.searchParams.set('offer', offerId);
    return `${target.pathname}${target.search}${target.hash}`;
  }
}
