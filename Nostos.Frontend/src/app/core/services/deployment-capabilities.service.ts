import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, shareReplay, tap } from 'rxjs';

import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';

/**
 * Runtime deployment contract reported by the Nostos server.
 *
 * The Angular bundle is identical for SelfHosted and Cloud. Consumers use
 * these server-authoritative product capabilities instead of build flags,
 * hostname checks, or assumptions about infrastructure vendors.
 */
@Injectable({ providedIn: 'root' })
export class DeploymentCapabilitiesService {
  private readonly http = inject(HttpClient);
  private capabilities$?: Observable<DeploymentCapabilities>;
  private latest: DeploymentCapabilities | null = null;

  get(refresh = false): Observable<DeploymentCapabilities> {
    if (refresh || !this.capabilities$) {
      this.capabilities$ = this.http
        .get<DeploymentCapabilities>('/api/runtime/capabilities')
        .pipe(
          tap((capabilities) => (this.latest = capabilities)),
          shareReplay({ bufferSize: 1, refCount: false }),
        );
    }

    return this.capabilities$;
  }

  /**
   * The answer already loaded by app bootstrap, without starting a request.
   *
   * A path decision that must be taken synchronously (which upload contract a
   * page will use) cannot wait on an observable. `null` means "not known yet";
   * callers fail closed to the older, always-present behaviour rather than
   * blocking a click.
   */
  peek(): DeploymentCapabilities | null {
    return this.latest;
  }
}
