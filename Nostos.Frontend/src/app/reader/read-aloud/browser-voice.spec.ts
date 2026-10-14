import { BrowserVoice, pickBrowserVoice } from './browser-voice';

describe('pickBrowserVoice', () => {
  const voices = [
    { name: 'Alice', lang: 'it-IT' },
    { name: 'Samantha', lang: 'en-US', default: true },
    { name: 'Daniel', lang: 'en-GB' },
    { name: 'Google UK English Female', lang: 'en-GB' },
    { name: 'Microsoft Elsa Online (Natural)', lang: 'it_IT' },
  ];

  it('never picks a voice for another language', () => {
    expect(pickBrowserVoice(voices, 'sv')).toBeNull();
  });

  it('prefers the exact region over the bare language', () => {
    expect(pickBrowserVoice(voices, 'en-US')?.name).toBe('Samantha');
  });

  it('prefers a platform’s better tier among voices for the language', () => {
    expect(pickBrowserVoice(voices, 'en-GB')?.name).toBe('Google UK English Female');
    expect(pickBrowserVoice(voices, 'it')?.name).toBe('Microsoft Elsa Online (Natural)');
  });

  it('leaves the choice to the browser when the book declares no language', () => {
    expect(pickBrowserVoice(voices, null)).toBeNull();
  });
});

describe('BrowserVoice', () => {
  function setup() {
    const utterances: any[] = [];
    const synth = {
      spoken: [] as any[],
      cancelled: 0,
      speak(utterance: any) {
        this.spoken.push(utterance);
      },
      cancel() {
        this.cancelled++;
      },
      getVoices: () => [{ name: 'Alice', lang: 'it-IT' }],
    };
    const voice = new BrowserVoice(synth, (text) => {
      const utterance = { text, lang: '', rate: 1, voice: null, onend: null, onerror: null };
      utterances.push(utterance);
      return utterance;
    });
    return { voice, synth, utterances };
  }

  it('speaks with the book’s language, a matching voice and a clamped speed', () => {
    const { voice, synth, utterances } = setup();

    void voice.speak('Quel ramo del lago di Como.', { language: 'it', rate: 5 });

    expect(synth.spoken).toHaveLength(1);
    expect(utterances[0].lang).toBe('it');
    expect(utterances[0].voice).toEqual({ name: 'Alice', lang: 'it-IT' });
    expect(utterances[0].rate).toBe(2);
  });

  it('resolves when the engine finishes the sentence', async () => {
    const { voice, utterances } = setup();
    const outcome = voice.speak('One.', { language: null, rate: 1 });

    utterances[0].onend();

    await expect(outcome).resolves.toBe('ended');
  });

  it('reports a cancelled sentence as cancelled, not as a failure', async () => {
    const { voice, synth } = setup();
    const outcome = voice.speak('One.', { language: null, rate: 1 });

    voice.cancel();

    await expect(outcome).resolves.toBe('cancelled');
    expect(synth.cancelled).toBeGreaterThan(0);
  });

  it('reports an engine error as a failure', async () => {
    const { voice, utterances } = setup();
    const outcome = voice.speak('One.', { language: null, rate: 1 });

    utterances[0].onerror({ error: 'synthesis-failed' });

    await expect(outcome).resolves.toBe('failed');
  });

  it('does not stall the book when the engine never reports the end', async () => {
    vi.useFakeTimers();
    try {
      const { voice } = setup();
      const outcome = voice.speak('One.', { language: null, rate: 1 });

      vi.advanceTimersByTime(60_000);

      await expect(outcome).resolves.toBe('ended');
    } finally {
      vi.useRealTimers();
    }
  });

  it('unlocks speech with a silent request inside the starting tap', () => {
    const { voice, synth, utterances } = setup();

    voice.prime();

    expect(synth.spoken).toHaveLength(1);
    expect(utterances[0].text).toBe('');
    expect(utterances[0].volume).toBe(0);
  });

  it('fails cleanly where the device has no speech engine', async () => {
    const voice = new BrowserVoice(null);
    await expect(voice.speak('One.', { language: null, rate: 1 })).resolves.toBe('failed');
  });
});
