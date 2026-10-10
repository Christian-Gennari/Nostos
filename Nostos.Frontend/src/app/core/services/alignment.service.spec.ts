import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { AlignmentService } from './alignment.service';
import { LocateTextResult } from '../dtos/alignment.dtos';

describe('AlignmentService', () => {
  let service: AlignmentService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });

    service = TestBed.inject(AlignmentService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('posts the audiobook position and EPUB edition ID', () => {
    let received: LocateTextResult | undefined;

    service.locateText('audio-1', {
      positionSeconds: 3721.5,
      targetBookId: 'epub-2',
    }).subscribe(result => received = result);

    const request = http.expectOne(
      '/api/books/audio-1/alignment/locate-text',
    );

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      positionSeconds: 3721.5,
      targetBookId: 'epub-2',
    });

    const result: LocateTextResult = {
      located: true,
      score: 0.96,
      excerpt: 'Sing, goddess, the anger of Achilles.',
      locator: {
        resourceHref: 'Text/chapter01.xhtml',
        spineIndex: 0,
        textOffset: 128,
        cfi: 'epubcfi(/6/2!/4/2:128)',
      },
    };

    request.flush(result);

    expect(received).toEqual(result);
  });

  it('preserves a declined result without turning it into an error', () => {
    let received: LocateTextResult | undefined;

    service.locateText('audio-1', {
      positionSeconds: 45,
      targetBookId: 'epub-2',
    }).subscribe(result => received = result);

    const request = http.expectOne(
      '/api/books/audio-1/alignment/locate-text',
    );

    request.flush({
      located: false,
      reason: 'ambiguous_match',
    });

    expect(received).toEqual({
      located: false,
      reason: 'ambiguous_match',
    });
  });

  it('preserves typed ProblemDetails errors', () => {
    let code: string | undefined;

    service.locateText('audio-1', {
      positionSeconds: 45,
      targetBookId: 'epub-2',
    }).subscribe({
      error: error => code = error.error?.title,
    });

    const request = http.expectOne(
      '/api/books/audio-1/alignment/locate-text',
    );

    request.flush(
      {
        title: 'stt_not_configured',
        status: 503,
        detail: 'Transcription is not configured.',
      },
      {
        status: 503,
        statusText: 'Service Unavailable',
      },
    );

    expect(code).toBe('stt_not_configured');
  });
});
