import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { READ_ALOUD_UNAVAILABLE, ReadAloudService, ReadAloudSpeechError } from './read-aloud.service';

/**
 * The reader falls back to the device voice on any failure, so this service
 * must turn every failure into data it can branch on.
 */
describe('ReadAloudService', () => {
  let service: ReadAloudService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ReadAloudService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks for the voices of the book’s language', async () => {
    const pending = firstValueFrom(service.status('en-GB'));
    const request = http.expectOne((r) => r.url === '/api/read-aloud/status');
    expect(request.request.params.get('language')).toBe('en-GB');
    request.flush({ available: true, managed: true, defaultVoice: 'bf_emma', voices: [], maxCharacters: 1200 });

    expect((await pending).defaultVoice).toBe('bf_emma');
  });

  it('treats a server that cannot answer as having no voice', async () => {
    const pending = firstValueFrom(service.status(null));
    http.expectOne((r) => r.url === '/api/read-aloud/status').flush('nope', { status: 500, statusText: 'Server Error' });

    expect(await pending).toEqual(READ_ALOUD_UNAVAILABLE);
  });

  it('returns the audio for one sentence', async () => {
    const pending = service.speech('Mr. Bennet made no answer.', 'bm_george', 'en-GB');
    const request = http.expectOne('/api/read-aloud/speech');
    expect(request.request.body).toEqual({
      text: 'Mr. Bennet made no answer.',
      voice: 'bm_george',
      language: 'en-GB',
    });
    request.flush(new Blob(['audio'], { type: 'audio/mpeg' }));

    expect((await pending).size).toBe(5);
  });

  it('reports a refusal with the server’s stable code', async () => {
    const pending = service.speech('One.', null, 'en');
    http.expectOne('/api/read-aloud/speech').flush(
      new Blob([JSON.stringify({ title: 'ai_usage_limit_reached', status: 429 })], {
        type: 'application/problem+json',
      }),
      { status: 429, statusText: 'Too Many Requests' },
    );

    const error = await pending.catch((caught) => caught);
    expect(error).toBeInstanceOf(ReadAloudSpeechError);
    expect(error.code).toBe('ai_usage_limit_reached');
  });

  it('reports an unreadable failure with no code rather than throwing something else', async () => {
    const pending = service.speech('One.', null, 'en');
    http.expectOne('/api/read-aloud/speech').flush(new Blob(['<html>'], { type: 'text/html' }), {
      status: 502,
      statusText: 'Bad Gateway',
    });

    const error = await pending.catch((caught) => caught);
    expect(error).toBeInstanceOf(ReadAloudSpeechError);
    expect(error.code).toBeNull();
  });
});
