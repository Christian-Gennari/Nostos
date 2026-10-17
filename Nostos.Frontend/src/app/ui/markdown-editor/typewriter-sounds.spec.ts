import { TypewriterSounds, soundForInput } from './typewriter-sounds';

describe('typewriter sounds (issue #1042)', () => {
  describe('soundForInput', () => {
    it('maps what the keyboard did to the text onto a sound', () => {
      expect(soundForInput('insertText', 'a')).toBe('key');
      expect(soundForInput('insertText', ' ')).toBe('space');
      expect(soundForInput('insertParagraph', null)).toBe('return');
      expect(soundForInput('insertLineBreak', null)).toBe('return');
      expect(soundForInput('deleteContentBackward', null)).toBe('backspace');
      expect(soundForInput('insertFromPaste', 'pasted')).toBeNull();
      expect(soundForInput('historyUndo', null)).toBeNull();
    });
  });

  describe('TypewriterSounds', () => {
    let started: number;
    let fetches: string[];
    let contexts: number;

    beforeEach(() => {
      started = 0;
      fetches = [];
      contexts = 0;
      class FakeContext {
        state = 'running';
        destination = {};
        constructor() {
          contexts++;
        }
        createBufferSource() {
          return { buffer: null, playbackRate: { value: 1 }, connect: () => undefined, start: () => started++ };
        }
        createGain() {
          return { gain: { value: 1 }, connect: () => undefined };
        }
        decodeAudioData() {
          return Promise.resolve({} as AudioBuffer);
        }
        resume() {
          return Promise.resolve();
        }
        suspend() {
          return Promise.resolve();
        }
        close() {
          return Promise.resolve();
        }
      }
      vi.stubGlobal('AudioContext', FakeContext);
      vi.stubGlobal(
        'fetch',
        vi.fn((url: string) => {
          fetches.push(url);
          return Promise.resolve({ ok: true, arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)) });
        }),
      );
    });

    afterEach(() => vi.unstubAllGlobals());

    it('creates nothing and fetches nothing until asked', () => {
      new TypewriterSounds();
      expect(contexts).toBe(0);
      expect(fetches).toEqual([]);
    });

    it('fetches the eight CC0 samples without an audio context, and plays after a key', async () => {
      const sounds = new TypewriterSounds(() => 0.5);
      await sounds.prefetch();
      expect(contexts).toBe(0);
      expect(fetches.length).toBe(8);
      expect(fetches.every((u) => u.startsWith('/audio/typewriter/') && u.endsWith('.mp3'))).toBe(true);

      sounds.play('key', 0.6, 97); // first key: the context starts, samples decode
      await new Promise((r) => setTimeout(r));
      sounds.play('key', 0.6, 97);
      expect(started).toBe(1);
      await sounds.prefetch();
      expect(fetches.length).toBe(8);
    });

    it('tries a sample that failed to load again on a later key', async () => {
      let fail = true;
      vi.stubGlobal(
        'fetch',
        vi.fn((url: string) => {
          fetches.push(url);
          return fail
            ? Promise.reject(new Error('offline'))
            : Promise.resolve({ ok: true, arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)) });
        }),
      );
      const sounds = new TypewriterSounds();
      sounds.play('key', 0.6, 97);
      await new Promise((r) => setTimeout(r, 10));
      fail = false;
      sounds.play('key', 0.6, 97);
      await new Promise((r) => setTimeout(r, 10));
      sounds.play('key', 0.6, 97);
      expect(started).toBe(1);
    });

    it('makes one sound for keys closer together than a carriage could strike', async () => {
      const sounds = new TypewriterSounds();
      sounds.play('key', 0.6, 1); // starts the context and the decoding
      await new Promise((r) => setTimeout(r));
      sounds.play('key', 0.6, 97);
      sounds.play('key', 0.6, 98);
      expect(started).toBe(1);
      await new Promise((r) => setTimeout(r, 40));
      sounds.play('space', 0.6);
      expect(started).toBe(2);
    });

    it('stays silent where audio is unavailable', () => {
      vi.stubGlobal('AudioContext', undefined);
      expect(() => new TypewriterSounds().play('key', 1)).not.toThrow();
    });
  });
});
