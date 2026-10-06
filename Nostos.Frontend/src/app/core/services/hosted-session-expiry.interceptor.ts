import { Injector, inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

import { CloudAuthService } from './cloud-auth.service';

/** Same-origin API authentication failures invalidate optional host context. */
export const hostedSessionExpiryInterceptor: HttpInterceptorFn = (request, next) => {
  const injector = inject(Injector);
  return next(request).pipe(catchError((error: unknown) => {
    const url = new URL(request.url, globalThis.location.origin);
    if (error instanceof HttpErrorResponse && error.status === 401
      && url.origin === globalThis.location.origin
      && (url.pathname === '/api' || url.pathname.startsWith('/api/'))) {
      // Resolve lazily: CloudAuthService itself uses HttpClient.
      injector.get(CloudAuthService).invalidateSession();
    }
    return throwError(() => error);
  }));
};
