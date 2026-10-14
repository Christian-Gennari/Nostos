import { AudioElementLike, ServerVoice } from './server-voice';

class FakeAudio implements AudioElementLike {
  src = '';
  playbackRate = 1;
  preservesPitch?: boolean;
  onended: (() => void) | null = null;
  onerror: (() => void) | null = null;
  plays: string[] = [];
  paused = 0;
  refuse = false;

  play(): Promise<void> {
    this.plays.push(this.src);
    return this.refuse ? Promise.reject(new Error('NotAllowedError')) : Promise.resolve();
  }

  pause(): void {
    this.paused++;
  }
}

function setup(fetchAudio?: (text: string) => Promise<Blob>) {
  const audio = new FakeAudio();
  const requested: string[] = [];
  const urls = {
    created: 0,
    revoked: [] as string[],
    createObjectURL: () => `blob:sentence-${++urls.created}`,
    revokeObjectURL: (url: string) => void urls.revoked.push(url),
  };
  const voice = new ServerVoice(
    (text) => {
      requested.push(text);
      return fetchAudio ? fetchAudio(text) : Promise.resolve(new Blob([text]));
    },
    audio,
    urls,
  );
  return { voice, audio, requested, urls };
}

const settled = async () => {
  for (let i = 0; i < 6; i++) await Promise.resolve();
};

describe('ServerVoice', () => {
  it('fetches the sentence, plays it at the chosen speed and resolves when it ends', async () => {
    const { voice, audio, requested, urls } = setup();

    const outcome = voice.speak('One.', { language: 'en', rate: 1.5 });
    await settled();

    expect(requested).toEqual(['One.']);
    expect(audio.src).toBe('blob:sentence-1');
    expect(audio.playbackRate).toBe(1.5);
    expect(audio.preservesPitch).toBe(true);

    audio.onended!();
    await expect(outcome).resolves.toBe('ended');
    expect(urls.revoked).toEqual(['blob:sentence-1']);
  });

  it('gets ahead of playback, and does not fetch a prepared sentence twice', async () => {
    const { voice, requested } = setup();

    voice.prepare(['Two.', 'Three.']);
    void voice.speak('Two.', { language: 'en', rate: 1 });
    await settled();

    expect(requested).toEqual(['Two.', 'Three.']);
  });

  it('keeps recent sentences so going back costs nothing, within a bound', async () => {
    const { voice, requested } = setup();

    voice.prepare(Array.from({ length: 20 }, (_, index) => `Sentence ${index}.`));
    void voice.speak('Sentence 19.', { language: 'en', rate: 1 });
    await settled();
    expect(requested).toHaveLength(20);

    voice.cancel();
    void voice.speak('Sentence 0.', { language: 'en', rate: 1 });
    await settled();
    expect(requested).toHaveLength(21);
  });

  it('fetches again after forgetting, so a new voice is heard from the next sentence', async () => {
    const { voice, requested } = setup();

    voice.prepare(['One.', 'Two.']);
    void voice.speak('One.', { language: 'en', rate: 1 });
    await settled();
    expect(requested).toEqual(['One.', 'Two.']);

    voice.cancel();
    voice.forget();
    void voice.speak('One.', { language: 'en', rate: 1 });
    voice.prepare(['Two.']);
    await settled();
    expect(requested).toEqual(['One.', 'Two.', 'One.', 'Two.']);
  });

  it('reports a refused request with the server’s code, and asks again next time', async () => {
    let fail = true;
    const { voice, requested } = setup((text) =>
      fail ? Promise.reject({ code: 'ai_usage_limit_reached' }) : Promise.resolve(new Blob([text])),
    );

    await expect(voice.speak('One.', { language: 'en', rate: 1 })).resolves.toBe('failed');
    expect(voice.lastFailure()).toBe('ai_usage_limit_reached');

    fail = false;
    void voice.speak('One.', { language: 'en', rate: 1 });
    await settled();
    expect(requested).toEqual(['One.', 'One.']);
    expect(voice.lastFailure()).toBeNull();
  });

  it('fails, so the reader can fall back, when the browser refuses to play', async () => {
    const { voice, audio } = setup();
    audio.refuse = true;

    await expect(voice.speak('One.', { language: 'en', rate: 1 })).resolves.toBe('failed');
    expect(voice.lastFailure()).toBe('read_aloud_playback_blocked');
  });

  it('stops at once when cancelled while playing', async () => {
    const { voice, audio } = setup();
    const outcome = voice.speak('One.', { language: 'en', rate: 1 });
    await settled();

    voice.cancel();

    await expect(outcome).resolves.toBe('cancelled');
    expect(audio.paused).toBe(1);
  });

  it('does not start a sentence whose audio arrives after it was cancelled', async () => {
    let deliver!: (blob: Blob) => void;
    const { voice, audio } = setup(() => new Promise<Blob>((resolve) => (deliver = resolve)));
    const outcome = voice.speak('One.', { language: 'en', rate: 1 });

    voice.cancel();
    deliver(new Blob(['One.']));

    await expect(outcome).resolves.toBe('cancelled');
    expect(audio.plays).toEqual([]);
  });

  it('unlocks playback with a moment of silence inside the starting tap', () => {
    const { voice, audio } = setup();

    voice.prime();

    expect(audio.plays).toHaveLength(1);
    expect(audio.plays[0].startsWith('data:audio/wav')).toBe(true);
  });
});
