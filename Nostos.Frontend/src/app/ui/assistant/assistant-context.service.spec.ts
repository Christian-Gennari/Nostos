import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { EMPTY, of } from 'rxjs';

import {
  AssistantContext,
  AssistantContextService,
  resolveAnchor,
} from './assistant-context.service';
import { BooksService } from '../../core/services/books.service';
import { Book } from '../../core/dtos/book.dtos';

/** A full context with everything unset, so each test states only what it means. */
function context(overrides: Partial<AssistantContext> = {}): AssistantContext {
  return {
    surface: 'home',
    route: '/',
    bookId: null,
    bookTitle: null,
    bookFormat: null,
    readerType: null,
    epubCfi: null,
    pdfPage: null,
    audioTimestamp: null,
    audioChapter: null,
    selectedText: null,
    readingTarget: null,
    brainReviewNoteId: null,
    topic: null,
    collectionId: null,
    anchor: null,
    ...overrides,
  };
}

/**
 * Builds a context service whose Router is a fixed-URL stub (so a route can be
 * stated without booting real routes) and whose BooksService, when supplied, is
 * a controllable fetch.
 */
function configure(url: string, books?: Partial<BooksService>): AssistantContextService {
  TestBed.configureTestingModule({
    providers: [
      { provide: Router, useValue: { url, events: EMPTY } },
      ...(books ? [{ provide: BooksService, useValue: books }] : []),
    ],
  });
  return TestBed.inject(AssistantContextService);
}

describe('AssistantContextService', () => {
  it('derives the surface and book id from the route', () => {
    const service = configure('/read/route-book');
    const resolved = service.context();

    expect(resolved.surface).toBe('reader');
    expect(resolved.bookId).toBe('route-book');
    expect(resolved.readingTarget).toBe('route-book');
  });

  it('resolves explicit providers ahead of ambient ones, first non-null per field', () => {
    const service = configure('/read/route-book');

    // Ambient provider answers first in registration order, but an explicit
    // provider is ordered ahead of it and therefore wins the shared fields.
    service.register(() => ({ bookId: 'ambient-book', bookTitle: 'Ambient' }));
    service.register(
      () => ({ bookId: 'explicit-book', bookTitle: 'Explicit' }),
      { explicit: true },
    );

    const resolved = service.context();
    expect(resolved.bookId).toBe('explicit-book');
    expect(resolved.bookTitle).toBe('Explicit');

    // A later ambient provider still fills a field the explicit one left null.
    service.register(() => ({ selectedText: 'quietly quoted' }));
    expect(service.context().selectedText).toBe('quietly quoted');
  });

  it('falls back to the route-derived base when no provider answers', () => {
    const service = configure('/library');
    const resolved = service.context();

    expect(resolved.surface).toBe('library');
    expect(resolved.bookId).toBeNull();
  });

  it('stops using a provider once it is unregistered', () => {
    const service = configure('/library');
    const off = service.register(() => ({ topic: 'stoicism' }));

    expect(service.context().topic).toBe('stoicism');
    off();
    expect(service.context().topic).toBeNull();
  });

  it('survives a provider that throws', () => {
    const service = configure('/library');
    service.register(() => {
      throw new Error('one broken reader must not blank the context');
    });
    service.register(() => ({ topic: 'resilience' }));

    expect(service.context().topic).toBe('resilience');
  });

  it('reads a book title and format from the route book id', () => {
    const service = configure('/read/route-book', {
      get: () =>
        of({ id: 'route-book', title: 'Route Book', type: 'ebook' } as unknown as Book),
    });
    TestBed.flushEffects();

    expect(service.context().bookTitle).toBe('Route Book');
    expect(service.context().bookFormat).toBe('ebook');
  });

  it('matches a route book id case-insensitively (deep links carry any casing)', () => {
    // The API returns a lowercased GUID while the URL may not be; the two must
    // still be treated as the same book or the title/format vanish.
    const service = configure('/read/4776D8AE-E9D5-4CBF-ABFD-56C53B00C592', {
      get: () =>
        of({
          id: '4776d8ae-e9d5-4cbf-abfd-56c53b00c592',
          title: 'Pride and Prejudice',
          type: 'ebook',
        } as unknown as Book),
    });
    TestBed.flushEffects();

    expect(service.context().bookTitle).toBe('Pride and Prejudice');
    expect(service.context().bookFormat).toBe('ebook');
  });
});

describe('resolveAnchor', () => {
  it('verifies an app-known EPUB CFI', () => {
    expect(resolveAnchor(context({ epubCfi: 'epubcfi(/6/4!/2/2)' }))).toEqual({
      kind: 'epub_cfi',
      value: 'epubcfi(/6/4!/2/2)',
      verified: true,
    });
  });

  it('turns a PDF page number into a verified string anchor', () => {
    expect(resolveAnchor(context({ pdfPage: 183 }))).toEqual({
      kind: 'pdf_page',
      value: '183',
      verified: true,
    });
  });

  it('uses the in-app audio timestamp when the audio reader publishes one', () => {
    expect(resolveAnchor(context({ audioTimestamp: 754 }))).toEqual({
      kind: 'audio_timestamp',
      value: '754',
      verified: true,
    });
  });

  it('returns null rather than guessing when no location is known', () => {
    expect(resolveAnchor(context())).toBeNull();
  });
});
