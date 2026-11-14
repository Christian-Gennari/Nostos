import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { NotesService } from './notes.service';

describe('NotesService', () => {
  let service: NotesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(NotesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** `/api/notes` is read with filter params, so match the path alone. */
  const notesPath = () => http.expectOne((request) => request.url === '/api/notes');

  it('reads the notes of one book, and its chapter titles', () => {
    service.list('b1').subscribe();
    const list = http.expectOne('/api/books/b1/notes');
    expect(list.request.method).toBe('GET');
    list.flush([]);

    service.sections('b1').subscribe();
    const sections = http.expectOne('/api/books/b1/sections');
    expect(sections.request.method).toBe('GET');
    sections.flush({ titles: ['I', null] });
  });

  it('reads one note, a passage, and several notes by id', () => {
    service.get('n1').subscribe();
    const note = http.expectOne('/api/notes/n1');
    expect(note.request.method).toBe('GET');
    note.flush({});

    service.getPassage('n1').subscribe();
    const passage = http.expectOne('/api/notes/n1/passage');
    expect(passage.request.method).toBe('GET');
    passage.flush({});

    service.getMany(['n1', 'n2']).subscribe();
    const many = http.expectOne((request) => request.url === '/api/notes/by-ids');
    expect(many.request.params.get('ids')).toBe('n1,n2');
    many.flush([]);
  });

  it('creates a note on its book and captures with its anchor', () => {
    service.create('b1', { content: 'A note' } as never).subscribe();
    const create = http.expectOne('/api/books/b1/notes');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ content: 'A note' });
    create.flush({});

    service
      .capture('b1', {
        content: 'A capture',
        selectedText: 'quoted',
        captureSource: 'assistant',
        sourceAnchorKind: 'epub_cfi',
        sourceAnchorValue: 'epubcfi(/6/4)',
        anchorVerified: true,
      })
      .subscribe();
    const capture = http.expectOne('/api/books/b1/notes');
    expect(capture.request.body).toEqual({
      content: 'A capture',
      selectedText: 'quoted',
      captureSource: 'assistant',
      sourceAnchorKind: 'epub_cfi',
      sourceAnchorValue: 'epubcfi(/6/4)',
      anchorVerified: true,
    });
    capture.flush({});
  });

  it('updates a note, its location, and deletes it', () => {
    service.update('n1', { content: 'Edited' } as never).subscribe();
    const update = http.expectOne('/api/notes/n1');
    expect(update.request.method).toBe('PUT');
    expect(update.request.body).toEqual({ content: 'Edited' });
    update.flush({});

    service.setLocation('n1', 'epubcfi(/6/4)').subscribe();
    const location = http.expectOne('/api/notes/n1/location');
    expect(location.request.method).toBe('PUT');
    expect(location.request.body).toEqual({ cfiRange: 'epubcfi(/6/4)' });
    location.flush({});

    service.delete('n1').subscribe();
    const removed = http.expectOne('/api/notes/n1');
    expect(removed.request.method).toBe('DELETE');
    removed.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('reads and restores a note’s raw transcript', () => {
    service.getOriginal('n1').subscribe();
    const raw = http.expectOne('/api/notes/n1/raw');
    expect(raw.request.method).toBe('GET');
    raw.flush({});

    service.restoreOriginal('n1').subscribe();
    const restore = http.expectOne('/api/notes/n1/raw/restore');
    expect(restore.request.method).toBe('POST');
    expect(restore.request.body).toEqual({});
    restore.flush({});
  });

  it('searches notes with a query and a limit', () => {
    service.search('virtue', 10).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/notes/search');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('query')).toBe('virtue');
    expect(request.request.params.get('limit')).toBe('10');
    request.flush([]);
  });

  it('browses with the default page and no filter it was not given', () => {
    service.browse().subscribe();

    const request = notesPath();
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('limit')).toBe('25');
    expect(request.request.params.get('offset')).toBe('0');
    expect(request.request.params.keys().sort()).toEqual(['limit', 'offset']);
    request.flush({ items: [], totalCount: 0, offset: 0, limit: 25 });
  });

  it('browses by book, work, kind and topic, and pages through the notes', () => {
    service
      .browse({
        bookId: 'b1',
        workId: 'w1',
        kind: 'passage',
        topicId: 't1',
        withTopicId: 't2',
        withoutTopics: true,
        query: '  virtue  ',
        order: 'oldest',
        limit: 200,
        offset: 400,
      })
      .subscribe();

    const request = notesPath();
    const params = request.request.params;
    expect(params.get('bookId')).toBe('b1');
    expect(params.get('workId')).toBe('w1');
    expect(params.get('kind')).toBe('passage');
    expect(params.get('topicId')).toBe('t1');
    expect(params.get('withTopicId')).toBe('t2');
    expect(params.get('withoutTopics')).toBe('true');
    expect(params.get('query')).toBe('virtue');
    expect(params.get('order')).toBe('oldest');
    expect(params.get('limit')).toBe('200');
    expect(params.get('offset')).toBe('400');
    request.flush({ items: [], totalCount: 0, offset: 400, limit: 200 });
  });

  it('reads the oldest first when asked, without an explicit order', () => {
    service.browse({ oldestFirst: true }).subscribe();

    const request = notesPath();
    expect(request.request.params.get('oldestFirst')).toBe('true');
    expect(request.request.params.has('order')).toBe(false);
    request.flush({ items: [], totalCount: 0, offset: 0, limit: 25 });
  });

  it('drops a blank query rather than sending it', () => {
    service.browse({ query: '   ' }).subscribe();

    const request = notesPath();
    expect(request.request.params.has('query')).toBe(false);
    request.flush({ items: [], totalCount: 0, offset: 0, limit: 25 });
  });

  it('lists the books a place holds, ignoring its own book filter', () => {
    service.books({ bookId: 'b1', kind: 'note' }).subscribe();

    const request = http.expectOne((candidate) => candidate.url === '/api/notes/books');
    expect(request.request.params.has('bookId')).toBe(false);
    expect(request.request.params.get('kind')).toBe('note');
    request.flush([]);
  });

  it('reads the counts', () => {
    service.summary().subscribe();

    const request = http.expectOne('/api/notes/summary');
    expect(request.request.method).toBe('GET');
    request.flush({ total: 0, passages: 0 });
  });

  it('passes a refused write through to the caller', () => {
    let status: number | undefined;
    service.create('b1', { content: '' } as never).subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/books/b1/notes').flush(
      { message: 'Empty' },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(status).toBe(400);
  });
});
