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
  // A repeated parameter arrives as a list; the first one counts, as before.
  const first = (value: unknown): string | null => {
    const one = Array.isArray(value) ? value[0] : value;
    return typeof one === 'string' && one ? one : null;
  };
  // A note is the more specific address, so it wins, as it did in the old Brain.
  const noteId = first(queryParams['noteId']);
  if (noteId) return router.createUrlTree(['/brain/notes', noteId]);
  const topicId = first(queryParams['topicId']) ?? first(queryParams['conceptId']);
  if (topicId) return router.createUrlTree(['/brain/topics', topicId]);
  return router.createUrlTree(['/brain']);
};
