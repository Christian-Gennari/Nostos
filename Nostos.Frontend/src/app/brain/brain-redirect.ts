import { inject } from '@angular/core';
import { Router, type RedirectFunction } from '@angular/router';

/**
 * Where an old `/second-brain` address goes now (docs/brain.md): a topic
 * (`?topicId`, or the older `?conceptId`) to its page, a note (`?noteId`) to
 * its page, anything else to All notes. Saved links, history and other
 * devices keep working.
 */
export const secondBrainRedirect: RedirectFunction = ({ queryParams }) => {
  const router = inject(Router);
  const topicId = queryParams['topicId'] ?? queryParams['conceptId'];
  if (typeof topicId === 'string' && topicId) return router.createUrlTree(['/brain/topics', topicId]);
  const noteId = queryParams['noteId'];
  if (typeof noteId === 'string' && noteId) return router.createUrlTree(['/brain/notes', noteId]);
  return router.createUrlTree(['/brain']);
};
