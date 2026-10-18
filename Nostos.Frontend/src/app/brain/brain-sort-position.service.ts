import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, switchMap } from 'rxjs';

import { CloudAuthService } from '../core/services/cloud-auth.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';

const STORAGE_PREFIX = 'nostos.brain.sortPosition.v1';

/** Where sorting one by one was left: the note in focus, in that queue. */
export interface SortPosition {
  noteId: string;
  book: string | null;
  order: string;
}

/**
 * Remembers which note "Sort one by one" was on, per account, so a reload or a
 * trip to edit a note comes back to it. On Cloud the key is the signed-in
 * account, so one account never resumes another's place; when the account is
 * unknown nothing is stored.
 */
@Injectable({ providedIn: 'root' })
export class BrainSortPositionService {
  private readonly capabilities = inject(DeploymentCapabilitiesService);
  private readonly cloudAuth = inject(CloudAuthService);

  private readonly key$: Observable<string | null> = this.capabilities.get().pipe(
    switchMap((capabilities) => {
      if (capabilities.deploymentMode === 'SelfHosted') return of('selfhosted');
      if (capabilities.deploymentMode !== 'Cloud') return of(null);
      return this.cloudAuth.getSession().pipe(
        map((session) => (session.authenticated && session.account?.id ? `account:${session.account.id}` : null)),
      );
    }),
    map((identity) => (identity ? `${STORAGE_PREFIX}:${encodeURIComponent(identity)}` : null)),
    catchError(() => of(null)),
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  read(): Observable<SortPosition | null> {
    return this.key$.pipe(map((key) => {
      if (!key) return null;
      try {
        const raw = localStorage.getItem(key);
        const value = raw ? (JSON.parse(raw) as Partial<SortPosition>) : null;
        return value && typeof value.noteId === 'string'
          ? { noteId: value.noteId, book: value.book ?? null, order: value.order ?? 'oldest' }
          : null;
      } catch {
        return null;
      }
    }));
  }

  write(position: SortPosition | null): void {
    this.key$.subscribe((key) => {
      if (!key) return;
      try {
        if (position) localStorage.setItem(key, JSON.stringify(position));
        else localStorage.removeItem(key);
      } catch {
        // Storage refused (private mode, quota): resuming is a convenience.
      }
    });
  }
}
