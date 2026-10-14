import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ReadAloudController } from './read-aloud-controller';
import {
  ReadAloudOutcome,
  ReadAloudSegment,
  ReadAloudSpeakOptions,
  ReadAloudVoice,
  ReaderReadAloud,
} from './read-aloud.types';

/** A book of numbered sentences with a cursor, as a reader would expose it. */
class FakeSource implements ReaderReadAloud {
  readonly moved = signal(0);
  readonly userMoved = this.moved.asReadonly();
  index = -1;
  ended = 0;
  /** Where `begin` lands: the first sentence of the "visible page". */
  pageStart = 0;

  constructor(readonly sentences: string[]) {}

  language(): string | null {
    return 'en';
  }

  async begin(): Promise<ReadAloudSegment | null> {
    this.index = this.pageStart;
    return this.at();
  }

  async step(direction: 1 | -1): Promise<ReadAloudSegment | null> {
    const target = this.index + direction;
    if (target < 0 || target >= this.sentences.length) return null;
    this.index = target;
    return this.at();
  }

  upcoming(count: number): string[] {
    return this.sentences.slice(this.index + 1, this.index + 1 + count);
  }

  end(): void {
    this.ended++;
  }

  private at(): ReadAloudSegment | null {
    const text = this.sentences[this.index];
    return text ? { text } : null;
  }
}

/** A voice the test finishes by hand, so the order of events is explicit. */
class FakeVoice implements ReadAloudVoice {
  spoken: Array<{ text: string; rate: number }> = [];
  prepared: string[][] = [];
  failAll = false;
  failure: string | null = null;
  primed = 0;
  private settle: ((outcome: ReadAloudOutcome) => void) | null = null;

  constructor(readonly id: string) {}

  private onStart: (() => void) | undefined;

  started(): void {
    this.onStart?.();
  }

  speak(text: string, options: ReadAloudSpeakOptions): Promise<ReadAloudOutcome> {
    this.spoken.push({ text, rate: options.rate });
    this.onStart = options.onStart;
    if (this.failAll) return Promise.resolve('failed');
    return new Promise((resolve) => {
      this.settle = resolve;
    });
  }

  prepare(texts: string[]): void {
    this.prepared.push(texts);
  }

  prime(): void {
    this.primed++;
  }

  lastFailure(): string | null {
    return this.failure;
  }

  cancel(): void {
    this.finish('cancelled');
  }

  finish(outcome: ReadAloudOutcome = 'ended'): void {
    const settle = this.settle;
    this.settle = null;
    settle?.(outcome);
  }

  get texts(): string[] {
    return this.spoken.map((entry) => entry.text);
  }
}

const settled = async () => {
  for (let i = 0; i < 6; i++) await Promise.resolve();
};

describe('ReadAloudController', () => {
  let controller: ReadAloudController;
  let source: FakeSource;
  let voice: FakeVoice;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [ReadAloudController] });
    controller = TestBed.inject(ReadAloudController);
    source = new FakeSource(['One.', 'Two.', 'Three.']);
    voice = new FakeVoice('device');
    controller.setVoices(voice);
    controller.attach(source);
  });

  afterEach(() => localStorage.clear());

  it('is offered only while a text reader is attached', () => {
    expect(controller.available()).toBe(true);
    controller.attach(null);
    expect(controller.available()).toBe(false);
  });

  it('reads sentence after sentence and stops at the end of the book', async () => {
    await controller.start();
    await settled();
    expect(controller.status()).toBe('playing');
    expect(voice.texts).toEqual(['One.']);

    voice.finish();
    await settled();
    voice.finish();
    await settled();
    expect(voice.texts).toEqual(['One.', 'Two.', 'Three.']);

    voice.finish();
    await settled();
    expect(controller.status()).toBe('idle');
    expect(source.ended).toBe(1);
  });

  it('primes the voice inside the tap that starts or resumes reading', async () => {
    void controller.start();
    expect(voice.primed).toBe(1);
    await settled();

    controller.pause();
    controller.resume();
    expect(voice.primed).toBe(2);
  });

  it('tells the voice what comes next so it can get ahead', async () => {
    await controller.start();
    await settled();
    expect(voice.prepared[0]).toEqual(['Two.', 'Three.']);
  });

  it('shows it is waiting until the voice actually starts', async () => {
    await controller.start();
    await settled();
    expect(controller.buffering()).toBe(true);

    voice.started();
    expect(controller.buffering()).toBe(false);

    voice.finish();
    await settled();
    expect(controller.buffering()).toBe(true);

    controller.pause();
    expect(controller.buffering()).toBe(false);
  });

  it('pauses on the current sentence and says it again on resume', async () => {
    await controller.start();
    await settled();
    voice.finish();
    await settled();

    controller.pause();
    await settled();
    expect(controller.status()).toBe('paused');

    controller.resume();
    await settled();
    expect(controller.status()).toBe('playing');
    expect(voice.texts).toEqual(['One.', 'Two.', 'Two.']);
  });

  it('skips forward and back while playing', async () => {
    await controller.start();
    await settled();

    controller.next();
    await settled();
    controller.next();
    await settled();
    controller.previous();
    await settled();

    expect(voice.texts).toEqual(['One.', 'Two.', 'Three.', 'Two.']);
    expect(controller.status()).toBe('playing');
  });

  it('stays on the sentence when there is nothing further that way', async () => {
    await controller.start();
    await settled();

    controller.previous();
    await settled();

    expect(voice.texts).toEqual(['One.', 'One.']);
    expect(controller.status()).toBe('playing');
  });

  it('moves the cursor without speaking while paused', async () => {
    await controller.start();
    await settled();
    controller.pause();

    controller.next();
    await settled();
    expect(voice.texts).toEqual(['One.']);

    controller.resume();
    await settled();
    expect(voice.texts).toEqual(['One.', 'Two.']);
  });

  it('stops, clears the mark and can start again', async () => {
    await controller.start();
    await settled();

    controller.stop();
    expect(controller.status()).toBe('idle');
    expect(source.ended).toBe(1);

    await controller.start();
    await settled();
    expect(voice.texts).toEqual(['One.', 'One.']);
  });

  it('applies a new speed to the sentence being spoken and remembers it', async () => {
    await controller.start();
    await settled();

    controller.setRate(1.5);
    await settled();

    expect(voice.spoken).toEqual([
      { text: 'One.', rate: 1 },
      { text: 'One.', rate: 1.5 },
    ]);
    expect(localStorage.getItem('nostos.readAloud.rate')).toBe('1.5');
  });

  it('cycles through the offered speeds', () => {
    controller.cycleRate();
    expect(controller.rate()).toBe(1.25);
    controller.setRate(1.5);
    controller.cycleRate();
    expect(controller.rate()).toBe(0.75);
  });

  it('brings a remembered speed above the fastest one offered down to it', () => {
    localStorage.setItem('nostos.readAloud.rate', '2');
    expect(TestBed.runInInjectionContext(() => new ReadAloudController()).rate()).toBe(1.5);
  });

  it('carries on from the new page when the person turns it', async () => {
    await controller.start();
    await settled();
    TestBed.tick();

    source.pageStart = 2;
    source.moved.update((count) => count + 1);
    TestBed.tick();
    await settled();

    expect(voice.texts).toEqual(['One.', 'Three.']);
    expect(controller.status()).toBe('playing');
  });

  it('falls back to the second voice, with a notice, when the first cannot speak', async () => {
    const server = new FakeVoice('server');
    server.failAll = true;
    server.failure = 'ai_usage_limit_reached';
    controller.setVoices(server, voice, (code) => `Reading with this device’s voice (${code}).`);

    await controller.start();
    await settled();

    expect(server.texts).toEqual(['One.']);
    expect(voice.texts).toEqual(['One.']);
    expect(controller.activeVoiceId()).toBe('device');
    expect(controller.notice()).toBe('Reading with this device’s voice (ai_usage_limit_reached).');
    expect(controller.status()).toBe('playing');
  });

  it('stops with a notice when no voice can speak', async () => {
    voice.failAll = true;

    await controller.start();
    await settled();

    expect(controller.status()).toBe('idle');
    expect(controller.notice()).toBe('Read aloud is not available on this device.');
  });

  it('says so when the page has nothing to read', async () => {
    controller.attach(new FakeSource([]));

    await controller.start();
    await settled();

    expect(controller.status()).toBe('idle');
    expect(controller.notice()).toBe('There is nothing to read aloud from here.');
  });

  it('stops when the reader goes away', async () => {
    await controller.start();
    await settled();

    controller.attach(null);

    expect(controller.status()).toBe('idle');
    expect(source.ended).toBe(1);
  });
});
