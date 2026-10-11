import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { AlignmentService } from './alignment.service';
import {
  LocateAudioResult,
  LocateTextResult,
} from '../dtos/alignment.dtos';

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

  it('preserves a declined text result without turning it into an error', () => {
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

  it('preserves typed ProblemDetails errors for locate-text', () => {
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

  it('posts the visible EPUB excerpt and target audiobook ID', () => {
    let received: LocateAudioResult | undefined;

    const excerpt =
      'The evening was quiet. The last light rested on the mountains.';

    service.locateAudio('epub-1', {
      targetBookId: 'audio-2',
      excerpt,
    }).subscribe(result => received = result);

    const request = http.expectOne(
      '/api/books/epub-1/alignment/locate-audio',
    );

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      targetBookId: 'audio-2',
      excerpt,
    });

    const result: LocateAudioResult = {
      located: true,
      positionSeconds: 3721.5,
      probes: 4,
      score: 0.96,
      anchorStored: true,
    };

    request.flush(result);

    expect(received).toEqual(result);
  });

  it('preserves an approximate audio position on a declined match', () => {
    let received: LocateAudioResult | undefined;

    service.locateAudio('epub-1', {
      targetBookId: 'audio-2',
      excerpt: 'The first sentence of the visible passage.',
    }).subscribe(result => received = result);

    http.expectOne('/api/books/epub-1/alignment/locate-audio')
      .flush({
        located: false,
        reason: 'no_confident_match',
        approximatePositionSeconds: 3600,
        probes: 5,
      });

    expect(received).toEqual({
      located: false,
      reason: 'no_confident_match',
      approximatePositionSeconds: 3600,
      probes: 5,
    });
  });

  it('preserves a declined audio match without an approximation', () => {
    let received: LocateAudioResult | undefined;

    service.locateAudio('epub-1', {
      targetBookId: 'audio-2',
      excerpt: 'An unmatched passage from the current page.',
    }).subscribe(result => received = result);

    http.expectOne('/api/books/epub-1/alignment/locate-audio')
      .flush({
        located: false,
        reason: 'insufficient_evidence',
        approximatePositionSeconds: null,
        probes: 3,
      });

    expect(received).toEqual({
      located: false,
      reason: 'insufficient_evidence',
      approximatePositionSeconds: null,
      probes: 3,
    });
  });

  it('preserves typed ProblemDetails errors for locate-audio', () => {
    let code: string | undefined;

    service.locateAudio('epub-1', {
      targetBookId: 'audio-2',
      excerpt: 'A passage to locate in the audiobook.',
    }).subscribe({
      error: error => code = error.error?.title,
    });

    http.expectOne('/api/books/epub-1/alignment/locate-audio')
      .flush(
        {
          title: 'alignment_transcription_not_configured',
          status: 503,
          detail: 'Alignment transcription is not configured.',
        },
        {
          status: 503,
          statusText: 'Service Unavailable',
        },
      );

    expect(code).toBe('alignment_transcription_not_configured');
  });

  it('does not automatically retry a failed audio alignment', () => {
    service.locateAudio('epub-1', {
      targetBookId: 'audio-2',
      excerpt: 'The current passage.',
    }).subscribe({ error: () => {} });

    const request = http.expectOne(
      '/api/books/epub-1/alignment/locate-audio',
    );

    request.flush(
      { title: 'alignment_operation_timeout', status: 504 },
      { status: 504, statusText: 'Gateway Timeout' },
    );

    http.expectNone('/api/books/epub-1/alignment/locate-audio');
  });
});
