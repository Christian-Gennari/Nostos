import { TypewriterSounds, soundForInput } from './typewriter-sounds';

describe('typewriter sounds (issues #1042, #1065)', () => {
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

    it('fetches the typewriter by default, without an audio context, and plays after a key', async () => {
      const sounds = new TypewriterSounds(() => 0.5);
      await sounds.prefetch();
      expect(contexts).toBe(0);
      // Twelve keys, space, return and backspace, from one machine.
      expect(fetches.length).toBe(15);
      expect(fetches.every((u) => u.startsWith('/audio/typewriter/olivetti-lettera-22/') && u.endsWith('.mp3'))).toBe(true);
      expect(fetches.filter((u) => /\/key-\d+\.mp3$/.test(u)).length).toBeGreaterThanOrEqual(8);
      expect(fetches.some((u) => u.endsWith('/return.mp3'))).toBe(true);

      sounds.play('key', 0.6, 97); // first key: the context starts, samples decode
      await new Promise((r) => setTimeout(r));
      sounds.play('key', 0.6, 97);
      expect(started).toBe(1);
      await sounds.prefetch();
      expect(fetches.length).toBe(15);
    });

    it('keeps the keyboard pack, fetched only once chosen', async () => {
      const sounds = new TypewriterSounds();
      sounds.setPack('keyboard');
      await sounds.prefetch();
      expect(fetches.length).toBe(8);
      expect(fetches.every((u) => u.startsWith('/audio/typewriter/cherry-kc1000/'))).toBe(true);
      sounds.play('key', 0.6, 1); // first key: the context starts, samples decode
      await new Promise((r) => setTimeout(r));
      sounds.play('return', 0.6);
      expect(started).toBe(1);
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

    it('makes one sound for one key reported twice, but keeps fast typing', async () => {
      const sounds = new TypewriterSounds();
      sounds.play('key', 0.6, 1); // starts the context and the decoding
      await new Promise((r) => setTimeout(r));
      sounds.play('key', 0.6, 97);
      sounds.play('key', 0.6, 98);
      expect(started).toBe(1);
      // 100+ wpm puts keys a few tens of milliseconds apart: every one sounds.
      await new Promise((r) => setTimeout(r, 20));
      sounds.play('space', 0.6);
      expect(started).toBe(2);
    });

    it('never throttles the carriage return, nor lets it silence a key', async () => {
      const sounds = new TypewriterSounds();
      sounds.play('key', 0.6, 1);
      await new Promise((r) => setTimeout(r));
      sounds.play('key', 0.6, 97);
      sounds.play('return', 0.6);
      sounds.play('key', 0.6, 98);
      expect(started).toBe(2);
      await new Promise((r) => setTimeout(r, 20));
      sounds.play('key', 0.6, 99);
      expect(started).toBe(3);
    });

    it('lets the settings preview a sound before any key has loaded it', async () => {
      const sounds = new TypewriterSounds();
      await sounds.preview('key', 0.6);
      expect(started).toBe(1);
      expect(contexts).toBe(1);
    });

    it('stays silent where audio is unavailable', () => {
      vi.stubGlobal('AudioContext', undefined);
      expect(() => new TypewriterSounds().play('key', 1)).not.toThrow();
    });
  });
});
