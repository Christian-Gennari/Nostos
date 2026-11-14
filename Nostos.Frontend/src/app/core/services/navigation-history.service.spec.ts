import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router } from '@angular/router';
import { Subject } from 'rxjs';

import { beforeEach, describe, expect, it } from 'vitest';

import { NavigationHistoryService } from './navigation-history.service';

describe('NavigationHistoryService', () => {
  let service: NavigationHistoryService;
  let events: Subject<NavigationEnd>;

  const visit = (url: string, afterRedirects = url) =>
    events.next(new NavigationEnd(1, url, afterRedirects));

  beforeEach(() => {
    events = new Subject<NavigationEnd>();
    TestBed.configureTestingModule({
      providers: [{ provide: Router, useValue: { events } }],
    });
    service = TestBed.inject(NavigationHistoryService);
  });

  it('reopens each app on its own entry point before it has been visited', () => {
    expect(service.getLastUrl('/library')).toBe('/library');
    expect(service.getLastUrl('/brain')).toBe('/brain');
    expect(service.getLastUrl('/studio')).toBe('/studio');
    // An app it does not know is its own entry point.
    expect(service.getLastUrl('/settings')).toBe('/settings');
  });

  it('remembers the last page of each app separately', () => {
    visit('/library/books/42');
    visit('/brain/topics/virtue');
    visit('/studio/writings/7');

    expect(service.getLastUrl('/library')).toBe('/library/books/42');
    expect(service.getLastUrl('/brain')).toBe('/brain/topics/virtue');
    expect(service.getLastUrl('/studio')).toBe('/studio/writings/7');
  });

  it('keeps the query a page was opened with', () => {
    visit('/library?filter=reading&sort=title');

    expect(service.getLastUrl('/library')).toBe('/library?filter=reading&sort=title');
  });

  it('reopens the Brain on the list the sorter sorts, not on the sorter', () => {
    visit('/brain/unsorted/sort');

    expect(service.getLastUrl('/brain')).toBe('/brain/unsorted');
  });

  it('uses where the router ended up, not where it was asked to go', () => {
    visit('/library/old', '/library/new');

    expect(service.getLastUrl('/library')).toBe('/library/new');
  });

  it('ignores screens that belong to no app', () => {
    visit('/library/books/42');
    visit('/settings');

    expect(service.getLastUrl('/library')).toBe('/library/books/42');
    expect(service.getLastUrl('/settings')).toBe('/settings');
  });

  it('takes the last navigation, not the first', () => {
    visit('/brain/a');
    visit('/brain/b');

    expect(service.getLastUrl('/brain')).toBe('/brain/b');
  });
});
