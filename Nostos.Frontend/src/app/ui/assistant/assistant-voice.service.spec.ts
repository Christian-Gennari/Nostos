import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import type { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';

import {
  AssistantVoiceService,
  TRANSCRIPTION_ENDPOINT,
} from './assistant-voice.service';

/**
 * These tests never touch a real microphone or the real endpoint: `getUserMedia`
 * and `MediaRecorder` are replaced with fakes, and `HttpTestingController`
 * stands in for the network.
 */

class FakeTrack {
  kind = 'audio';
  readyState: 'live' | 'ended' = 'live';
  stop = vi.fn(() => {
    this.readyState = 'ended';
  });
}

class FakeStream {
  readonly tracks: FakeTrack[];
  constructor(count = 1) {
    this.tracks = Array.from({ length: count }, () => new FakeTrack());
  }
  getTracks(): FakeTrack[] {
    return this.tracks;
  }
}

class FakeMediaRecorder {
  static instances: FakeMediaRecorder[] = [];
  static isTypeSupported = vi.fn(() => true);

  state: 'inactive' | 'recording' | 'paused' = 'inactive';
  mimeType = 'audio/webm';
  ondataavailable: ((event: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;

  constructor(readonly stream: FakeStream) {
    FakeMediaRecorder.instances.push(this);
  }

  start(): void {
    this.state = 'recording';
  }

  stop(): void {
    this.state = 'inactive';
    this.ondataavailable?.({
      data: new Blob(['audio-bytes'], { type: this.mimeType }),
    });
    this.onstop?.();
  }
}

const originalMediaDevices = Object.getOwnPropertyDescriptor(navigator, 'mediaDevices');

function stubMediaDevices(getUserMedia: unknown): void {
  const value = { getUserMedia };
  try {
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value });
  } catch {
    (navigator as unknown as { mediaDevices: unknown }).mediaDevices = value;
  }
}

/** Let a resolved promise's continuations run. */
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

describe('AssistantVoiceService', () => {
  let service: AssistantVoiceService;
  let http: HttpTestingController;
  let getUserMedia: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    FakeMediaRecorder.instances = [];
    getUserMedia = vi.fn();
    stubMediaDevices(getUserMedia);
    (globalThis as unknown as { MediaRecorder: unknown }).MediaRecorder = FakeMediaRecorder;

    URL.createObjectURL = vi.fn(() => 'blob:recording');
    URL.revokeObjectURL = vi.fn();

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AssistantVoiceService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    service.cancel();
    http.verify();
    vi.restoreAllMocks();
    if (originalMediaDevices) {
      Object.defineProperty(navigator, 'mediaDevices', originalMediaDevices);
    } else {
      delete (navigator as unknown as { mediaDevices?: unknown }).mediaDevices;
    }
    delete (globalThis as unknown as { MediaRecorder?: unknown }).MediaRecorder;
  });

  it('requests the microphone only on an explicit tap, then records', async () => {
    expect(getUserMedia).not.toHaveBeenCalled();
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();

    expect(getUserMedia).toHaveBeenCalledTimes(1);
    expect(getUserMedia).toHaveBeenCalledWith({ audio: true });
    expect(service.status()).toBe('recording');
    expect(service.isRecording()).toBe(true);
    expect(FakeMediaRecorder.instances).toHaveLength(1);
    expect(FakeMediaRecorder.instances[0].state).toBe('recording');
  });

  it('does not open the microphone once the surface is busy', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    service.start();
    await tick();

    expect(getUserMedia).toHaveBeenCalledTimes(1);
  });

  it('stop uploads multipart and hands the transcript back', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);
    const onTranscript = vi.fn();
    service.onTranscript = onTranscript;

    service.start();
    await tick();
    service.stop();

    expect(service.status()).toBe('transcribing');
    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    expect(request.request.method).toBe('POST');
    const body = request.request.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    expect(body.get('file')).toBeInstanceOf(Blob);
    expect((body.get('file') as Blob).size).toBeGreaterThan(0);

    request.flush({ text: '  the magic mountain  ', language: 'en', durationSeconds: 1.4 });

    expect(onTranscript).toHaveBeenCalledTimes(1);
    expect(onTranscript).toHaveBeenCalledWith('the magic mountain');
    expect(service.status()).toBe('idle');
    expect(service.error()).toBeNull();
  });

  it('uploads the browser MIME type including codec parameters', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    FakeMediaRecorder.instances[0].mimeType = 'audio/webm;codecs=opus';
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    const body = request.request.body as FormData;
    const file = body.get('file') as File;

    expect(file.type).toBe('audio/webm;codecs=opus');
    expect(file.name).toBe('voice-note.webm');

    request.flush({ text: 'browser audio', language: 'en', durationSeconds: 1 });
  });

  it('uses a filename that matches an mp4 recording container', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    FakeMediaRecorder.instances[0].mimeType = 'audio/mp4;codecs=opus';
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    const body = request.request.body as FormData;
    const file = body.get('file') as File;

    expect(file.type).toBe('audio/mp4;codecs=opus');
    expect(file.name).toBe('voice-note.m4a');

    request.flush({ text: 'mobile audio', language: 'en', durationSeconds: 1 });
  });

  it('cancel during recording uploads nothing and stops every track', async () => {
    const stream = new FakeStream(2);
    getUserMedia.mockResolvedValue(stream as unknown as MediaStream);
    const onTranscript = vi.fn();
    service.onTranscript = onTranscript;

    service.start();
    await tick();

    service.cancel();

    expect(service.status()).toBe('idle');
    expect(service.error()).toBeNull();
    expect(onTranscript).not.toHaveBeenCalled();
    http.expectNone(TRANSCRIPTION_ENDPOINT);
    for (const track of stream.tracks) {
      expect(track.stop).toHaveBeenCalledTimes(1);
      expect(track.readyState).toBe('ended');
    }
  });

  it('cancel during transcription discards the answer and stops the URL', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);
    const onTranscript = vi.fn();
    service.onTranscript = onTranscript;

    service.start();
    await tick();
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    service.cancel();

    // Aborting the subscription cancels the in-flight upload; a late answer can
    // no longer reach the conversation.
    expect(request.cancelled).toBe(true);
    expect(onTranscript).not.toHaveBeenCalled();
    expect(service.status()).toBe('idle');
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:recording');
  });

  it('surfaces a denial calmly and does not stick in an error state', async () => {
    getUserMedia.mockRejectedValueOnce({ name: 'NotAllowedError' });

    service.start();
    await tick();

    expect(service.status()).toBe('idle');
    expect(service.error()?.kind).toBe('denied');
    expect(service.error()?.message).not.toMatch(/DOMException|getUserMedia|NotAllowed/i);

    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);
    service.start();
    await tick();

    expect(service.error()).toBeNull();
    expect(service.status()).toBe('recording');
  });

  it('reports a missing device as no-device', async () => {
    getUserMedia.mockRejectedValue({ name: 'NotFoundError' });

    service.start();
    await tick();

    expect(service.error()?.kind).toBe('no-device');
    expect(service.status()).toBe('idle');
  });

  it('recovers from a failed transcription', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    request.flush('nope', { status: 500, statusText: 'Server Error' });

    expect(service.status()).toBe('idle');
    expect(service.error()?.kind).toBe('failed');

    service.start();
    await tick();
    expect(service.error()).toBeNull();
    expect(service.status()).toBe('recording');
  });

  it('surfaces an unconfigured STT provider instead of a generic failure', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    request.flush(
      {
        title: 'stt_not_configured',
        detail: 'Speech-to-text has no usable credential.',
      },
      { status: 503, statusText: 'Service Unavailable' },
    );

    expect(service.status()).toBe('idle');
    expect(service.error()?.kind).toBe('failed');
    expect(service.error()?.message).toBe(
      'Voice transcription is not configured yet. Check the STT provider in Settings.',
    );
  });

  it('surfaces an unsupported recording format distinctly', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    const request = http.expectOne(TRANSCRIPTION_ENDPOINT);
    request.flush(
      { title: 'stt_unsupported_format', detail: 'unsupported' },
      { status: 415, statusText: 'Unsupported Media Type' },
    );

    expect(service.error()?.message).toBe(
      "This browser's recording format is not supported by the transcription provider.",
    );
  });

  it.each([
    ['stt_permission_denied', 502, 'The transcription provider rejected its key. Check the STT provider in Settings.'],
    ['stt_rate_limited', 429, 'Voice transcription is busy right now. Try again in a moment.'],
    ['stt_provider_timeout', 504, 'The transcription provider took too long to answer. Try again.'],
  ])('tells a bad key, rate limiting and a timeout apart (%s)', async (code, status, message) => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    http.expectOne(TRANSCRIPTION_ENDPOINT).flush(
      { title: code, detail: 'provider detail' },
      { status, statusText: 'Error' },
    );

    expect(service.error()?.message).toBe(message);
  });

  it.each([
    ['stt_disabled', 503],
    ['stt_not_configured', 503],
    ['stt_permission_denied', 502],
  ])('on a managed deployment, does not send people to settings they do not have (%s, #1479)', async (code, status) => {
    TestBed.inject(DeploymentCapabilitiesService).peek = () =>
      ({ managedVoiceTranscription: true, canConfigureAiProvider: false }) as DeploymentCapabilities;
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    http.expectOne(TRANSCRIPTION_ENDPOINT).flush(
      { title: code, detail: 'provider detail' },
      { status, statusText: 'Error' },
    );

    expect(service.error()?.message).toBe('Voice transcription is temporarily unavailable.');
    expect(service.error()?.message).not.toContain('Settings');
  });

  it('leaves no track live after stop', async () => {
    const stream = new FakeStream(2);
    getUserMedia.mockResolvedValue(stream as unknown as MediaStream);

    service.start();
    await tick();
    expect(stream.tracks.every((track) => track.readyState === 'live')).toBe(true);

    service.stop();
    http.expectOne(TRANSCRIPTION_ENDPOINT).flush({
      text: 'a thought',
      language: 'en',
      durationSeconds: 1,
    });

    for (const track of stream.tracks) {
      expect(track.stop).toHaveBeenCalledTimes(1);
      expect(track.readyState).toBe('ended');
    }
  });

  it('revokes the local object URL after a successful transcription', async () => {
    getUserMedia.mockResolvedValue(new FakeStream() as unknown as MediaStream);

    service.start();
    await tick();
    service.stop();

    expect(URL.createObjectURL).toHaveBeenCalledTimes(1);

    http.expectOne(TRANSCRIPTION_ENDPOINT).flush({
      text: 'a thought',
      language: 'en',
      durationSeconds: 1,
    });

    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:recording');
  });

  it('treats an empty recording as a calm message, not an upload', async () => {
    const stream = new FakeStream() as unknown as MediaStream;
    getUserMedia.mockResolvedValue(stream);

    service.start();
    await tick();
    const recorder = FakeMediaRecorder.instances[0];
    recorder.ondataavailable = null; // no chunks are produced

    service.stop();

    http.expectNone(TRANSCRIPTION_ENDPOINT);
    expect(service.status()).toBe('idle');
    expect(service.error()?.kind).toBe('empty');
  });
});
