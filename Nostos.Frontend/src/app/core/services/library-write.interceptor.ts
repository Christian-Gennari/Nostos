import { inject } from '@angular/core';
import { HttpEventType, HttpInterceptorFn } from '@angular/common/http';
import { tap } from 'rxjs';

import {
  LIBRARY_REVISIONS_HEADER,
  LIBRARY_WRITE_HANDLED,
  LibraryChangesService,
} from './library-changes.service';

/**
 * Hands the revisions a handled write committed (see `LIBRARY_WRITE_HANDLED`)
 * to the change feed, so the tab does not re-read what it just wrote.
 */
export const libraryWriteInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.context.get(LIBRARY_WRITE_HANDLED)) return next(request);

  const changes = inject(LibraryChangesService);
  return next(request).pipe(
    tap((event) => {
      if (event.type === HttpEventType.Response) {
        changes.noteOwnWrite(event.headers.get(LIBRARY_REVISIONS_HEADER));
      }
    }),
  );
};
