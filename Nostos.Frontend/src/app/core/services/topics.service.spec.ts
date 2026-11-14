import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { TopicsService } from './topics.service';

describe('TopicsService', () => {
  let service: TopicsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TopicsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists the topics', () => {
    service.list().subscribe();

    const request = http.expectOne('/api/topics');
    expect(request.request.method).toBe('GET');
    request.flush([]);
  });

  it('reads one topic and the topics related to it', () => {
    service.get('t1').subscribe();
    const topic = http.expectOne('/api/topics/t1');
    expect(topic.request.method).toBe('GET');
    topic.flush({});

    service.getRelated('t1').subscribe();
    const related = http.expectOne('/api/topics/t1/related');
    expect(related.request.method).toBe('GET');
    related.flush([]);
  });

  it('reads the topic graph', () => {
    service.getGraph().subscribe();

    const request = http.expectOne('/api/topics/graph');
    expect(request.request.method).toBe('GET');
    request.flush({ nodes: [], edges: [] });
  });

  it('reads the writings that cite a topic’s notes, newest first', () => {
    const usagePath = () => http.expectOne((request) => request.url === '/api/topics/t1/writings');

    service.getWritingUsage('t1').subscribe();
    const byDefault = usagePath();
    expect(byDefault.request.params.get('limit')).toBe('5');
    byDefault.flush({ total: 0, writings: [], noteCitations: [] });

    service.getWritingUsage('t1', 10).subscribe();
    const chosen = usagePath();
    expect(chosen.request.params.get('limit')).toBe('10');
    chosen.flush({ total: 0, writings: [], noteCitations: [] });
  });

  it('renames a topic and sets its description', () => {
    service.rename('t1', 'Virtue').subscribe();
    const rename = http.expectOne('/api/topics/t1');
    expect(rename.request.method).toBe('PUT');
    expect(rename.request.body).toEqual({ topic: 'Virtue' });
    rename.flush({});

    service.setDescription('t1', 'A practice.').subscribe();
    const describe = http.expectOne('/api/topics/t1/description');
    expect(describe.request.method).toBe('PUT');
    expect(describe.request.body).toEqual({ description: 'A practice.' });
    describe.flush({});

    service.setDescription('t1', null).subscribe();
    const cleared = http.expectOne('/api/topics/t1/description');
    expect(cleared.request.body).toEqual({ description: null });
    cleared.flush({});
  });

  it('merges one topic into another', () => {
    service.merge('t1', 't2').subscribe();

    const request = http.expectOne('/api/topics/t1/merge');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ targetId: 't2' });
    request.flush({});
  });

  it('links and unlinks named topics on named notes', () => {
    service.link(['Virtue'], ['n1', 'n2']).subscribe();
    const link = http.expectOne('/api/topics/link');
    expect(link.request.method).toBe('POST');
    expect(link.request.body).toEqual({ topics: ['Virtue'], noteIds: ['n1', 'n2'] });
    link.flush({});

    service.unlink(['Virtue'], ['n1']).subscribe();
    const unlink = http.expectOne('/api/topics/unlink');
    expect(unlink.request.method).toBe('POST');
    expect(unlink.request.body).toEqual({ topics: ['Virtue'], noteIds: ['n1'] });
    unlink.flush({});
  });

  it('deletes a topic', () => {
    service.delete('t1').subscribe();

    const request = http.expectOne('/api/topics/t1');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('passes a refused merge through to the caller', () => {
    let status: number | undefined;
    service.merge('t1', 't1').subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/topics/t1/merge').flush(
      { message: 'Cannot merge a topic into itself' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(status).toBe(400);
  });
});
