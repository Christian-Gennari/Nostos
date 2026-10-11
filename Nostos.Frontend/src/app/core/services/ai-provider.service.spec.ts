import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AiProviderService } from './ai-provider.service';
import { AiProviderSettings } from '../dtos/ai-provider.dtos';

/**
 * The four routes behind the AI provider card. The important contract here is
 * write-only-ness: the service has no method that could carry a key back to the
 * browser, and `update` sends only the caller's body, so "leave the key alone"
 * is a decision the caller makes by omitting `apiKey`.
 */
describe('AiProviderService', () => {
  let service: AiProviderService;
  let http: HttpTestingController;

  const effective: AiProviderSettings = {
    llm: {
      enabled: true,
      baseUrl: 'http://omenhub:20128/v1',
      model: 'qwen3-32b',
      hasKey: true,
      keyFromServerEnv: false,
    },
    stt: {
      enabled: false,
      baseUrl: 'http://omenhub:20128',
      model: 'groq/whisper-large-v3-turbo',
      hasKey: false,
      keyFromServerEnv: false,
    },
    alignmentTranscription: {
      enabled: true,
      baseUrl: '',
      model: '',
      hasKey: false,
      keyFromServerEnv: false,
      useVoiceTranscription: true,
      apiStyle: 'openai-multipart',
    },
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(AiProviderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
  });

  it('reads the effective settings from the settings route', () => {
    let received: AiProviderSettings | undefined;
    service.get().subscribe((settings) => (received = settings));

    const request = http.expectOne('/api/settings/ai-provider');
    expect(request.request.method).toBe('GET');
    request.flush(effective);

    expect(received).toEqual(effective);
  });

  it('sends only the provided sections on update', () => {
    service.update({ llm: { model: 'gpt-4o' } }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider');
    expect(request.request.method).toBe('PUT');
    // No apiKey property at all: the stored key is left untouched.
    expect(request.request.body).toEqual({ llm: { model: 'gpt-4o' } });
    request.flush(effective);
  });

  it('passes an explicit clear through as an empty string', () => {
    service.update({ llm: { apiKey: '' } }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider');
    expect(request.request.body).toEqual({ llm: { apiKey: '' } });
    request.flush(effective);
  });

  it('updates alignment transcription without sending an untouched key', () => {
    service.update({
      alignmentTranscription: {
        useVoiceTranscription: false,
        apiStyle: 'json-base64',
        model: 'alignment-model',
      },
    }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      alignmentTranscription: {
        useVoiceTranscription: false,
        apiStyle: 'json-base64',
        model: 'alignment-model',
      },
    });
    expect(request.request.body.alignmentTranscription).not.toHaveProperty('apiKey');
    request.flush(effective);
  });

  it('passes an explicit alignment key clear through', () => {
    service.update({
      alignmentTranscription: { apiKey: '' },
    }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider');
    expect(request.request.body).toEqual({
      alignmentTranscription: { apiKey: '' },
    });
    request.flush(effective);
  });

  it('asks the models route for ids of one kind', () => {
    let models: string[] | undefined;
    service
      .loadModels({ kind: 'stt', baseUrl: 'http://omenhub:20128' })
      .subscribe((response) => (models = response.models));

    const request = http.expectOne('/api/settings/ai-provider/models');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      kind: 'stt',
      baseUrl: 'http://omenhub:20128',
    });
    request.flush({ models: ['whisper-1'] });

    expect(models).toEqual(['whisper-1']);
  });

  it('loads models for the alignment transcription kind', () => {
    service.loadModels({
      kind: 'alignmentStt',
      baseUrl: 'https://transcription.example/v1',
    }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider/models');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      kind: 'alignmentStt',
      baseUrl: 'https://transcription.example/v1',
    });
    request.flush({ models: ['alignment-model'] });
  });

  it('returns the test result even when the provider failed', () => {
    let ok: boolean | undefined;
    service.test({ kind: 'llm', model: 'qwen3-32b' }).subscribe((result) => (ok = result.ok));

    const request = http.expectOne('/api/settings/ai-provider/test');
    expect(request.request.method).toBe('POST');
    request.flush({ ok: false, error: '401 Unauthorized' });

    expect(ok).toBe(false);
  });

  it('tests alignment transcription using its own kind', () => {
    service.test({ kind: 'alignmentStt' }).subscribe();

    const request = http.expectOne('/api/settings/ai-provider/test');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ kind: 'alignmentStt' });
    request.flush({ ok: true, detail: 'Reached the voice transcription provider.' });
  });
});
