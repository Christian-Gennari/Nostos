import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter, type ActivatedRouteSnapshot } from '@angular/router';

import { routes } from '../app.routes';
import { secondBrainRedirect } from './brain-redirect';

describe('the /second-brain redirect', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  function landsOn(queryParams: Record<string, string>): string {
    const tree = TestBed.runInInjectionContext(() =>
      secondBrainRedirect({ queryParams } as unknown as Pick<ActivatedRouteSnapshot, 'queryParams'> as never));
    return TestBed.inject(Router).serializeUrl(tree as UrlTree);
  }

  it('is the route the old address takes', () => {
    const layout = routes.find((route) => route.children?.some((child) => child.path === 'second-brain'));
    expect(layout?.children?.find((child) => child.path === 'second-brain')?.redirectTo).toBe(secondBrainRedirect);
  });

  it('sends a topic to its page, by either old name', () => {
    expect(landsOn({ topicId: 't1' })).toBe('/brain/topics/t1');
    expect(landsOn({ conceptId: 'c1', mode: 'topics' })).toBe('/brain/topics/c1');
  });

  it('sends a note to its page, ahead of a topic', () => {
    expect(landsOn({ noteId: 'n1' })).toBe('/brain/notes/n1');
    expect(landsOn({ noteId: 'n1', topicId: 't1' })).toBe('/brain/notes/n1');
  });

  it('takes the first of a repeated parameter', () => {
    expect(landsOn({ topicId: ['t1', 't2'] } as unknown as Record<string, string>)).toBe('/brain/topics/t1');
  });

  it('sends anything else to All notes', () => {
    expect(landsOn({})).toBe('/brain');
    expect(landsOn({ mode: 'review' })).toBe('/brain');
  });
});
