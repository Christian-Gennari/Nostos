import { TrackPlaylistPlayer } from './track-playlist-player';

/**
 * A stand-in for HTMLAudioElement that behaves the way the element does where
 * it matters to a playlist: setting `src` unloads, `load()` leads to
 * `loadedmetadata`, and a track that runs out fires `pause` and then `ended`.
 */
class FakeAudio extends EventTarget {
  src = '';
  preload = '';
  currentTime = 0;
  paused = true;
  ended = false;
  playbackRate = 1;
  defaultPlaybackRate = 1;
  loads: string[] = [];
  playCalls = 0;
  failNextLoad = false;

  load(): void {
    if (!this.src) return;
    this.loads.push(this.src);
    this.paused = true;
    this.ended = false;
    this.currentTime = 0;
    // A new source resets the rate to the default, as the real element does.
    this.playbackRate = this.defaultPlaybackRate;
  }

  /** The browser finishes fetching the current source's metadata. */
  finishLoading(): void {
    if (this.failNextLoad) {
      this.failNextLoad = false;
      this.dispatchEvent(new Event('error'));
      return;
    }
    this.dispatchEvent(new Event('loadedmetadata'));
  }

  play(): Promise<void> {
    this.playCalls++;
    this.paused = false;
    this.ended = false;
    this.dispatchEvent(new Event('playing'));
    return Promise.resolve();
  }

  pause(): void {
    if (this.paused) return;
    this.paused = true;
    this.dispatchEvent(new Event('pause'));
  }

  removeAttribute(name: string): void {
    if (name === 'src') this.src = '';
  }

  /** The current track plays to its end. */
  runOut(): void {
    this.paused = true;
    this.ended = true;
    this.dispatchEvent(new Event('pause'));
    this.dispatchEvent(new Event('ended'));
  }
}

function setup(durations = [100, 250.5, 4]) {
  const audio = new FakeAudio();
  const events: string[] = [];
  const player = new TrackPlaylistPlayer(
    durations.map((duration, index) => ({ url: `/tracks/${index + 1}`, duration })),
    {
      onload: () => events.push('load'),
      onloaderror: () => events.push('loaderror'),
      onplay: () => events.push('play'),
      onpause: () => events.push('pause'),
      onend: () => events.push('end'),
    },
    () => audio as unknown as HTMLAudioElement,
  );
  return { audio, events, player };
}

describe('TrackPlaylistPlayer (multi-track audiobooks, issue #835)', () => {
  it('presents the tracks as one book and loads only the first', () => {
    const { audio, events, player } = setup();

    expect(player.duration()).toBe(354.5);
    expect(audio.loads).toEqual(['/tracks/1']);
    expect(events).toEqual([]);

    audio.finishLoading();
    expect(events).toEqual(['load']);
    expect(player.seek()).toBe(0);
  });

  it('reports the position in book seconds, not track seconds', () => {
    const { audio, player } = setup();
    audio.finishLoading();

    audio.currentTime = 12;
    expect(player.seek()).toBe(12);

    player.seek(200);
    expect(audio.loads).toEqual(['/tracks/1', '/tracks/2']);
    // While the new track loads, the position is already where the listener asked to be.
    expect(player.seek()).toBe(200);

    audio.finishLoading();
    expect(audio.currentTime).toBe(100);
    expect(player.seek()).toBe(200);
    expect(player.trackIndex()).toBe(1);
  });

  it('seeks inside the current track without reloading it', () => {
    const { audio, player } = setup();
    audio.finishLoading();

    player.seek(42);

    expect(audio.loads).toEqual(['/tracks/1']);
    expect(audio.currentTime).toBe(42);
  });

  it('lands on the right track at every boundary', () => {
    const { audio, player } = setup();
    audio.finishLoading();

    player.seek(100); // exactly where track 2 starts
    audio.finishLoading();
    expect(player.trackIndex()).toBe(1);
    expect(audio.currentTime).toBe(0);

    player.seek(99.999);
    audio.finishLoading();
    expect(player.trackIndex()).toBe(0);

    player.seek(350.5);
    audio.finishLoading();
    expect(player.trackIndex()).toBe(2);

    player.seek(9999); // clamped to the end of the book
    expect(player.seek()).toBe(354.5);
    player.seek(-5);
    audio.finishLoading();
    expect(player.seek()).toBe(0);
  });

  it('moves to the next track when one runs out, on the same audio element', () => {
    const { audio, events, player } = setup();
    audio.finishLoading();
    player.play();
    expect(events).toEqual(['load', 'play']);

    audio.currentTime = 100.02; // the file is a few ms longer than recorded
    expect(player.seek()).toBe(100); // never past the next track's start
    audio.runOut();

    // No pause reported, no end reported: the book is still playing.
    expect(events).toEqual(['load', 'play']);
    expect(player.playing()).toBe(true);
    expect(audio.loads).toEqual(['/tracks/1', '/tracks/2']);
    expect(player.seek()).toBe(100);

    audio.finishLoading();
    expect(audio.playCalls).toBe(2);
    expect(events).toEqual(['load', 'play']);
  });

  it('reports the end only after the last track', () => {
    const { audio, events, player } = setup([10, 20]);
    audio.finishLoading();
    player.play();
    audio.runOut();
    audio.finishLoading();
    audio.runOut();

    expect(events).toEqual(['load', 'play', 'end']);
    expect(player.playing()).toBe(false);
  });

  it('reports a pause the listener asked for, including one from the system controls', () => {
    const { audio, events, player } = setup();
    audio.finishLoading();
    player.play();

    player.pause();
    expect(events).toEqual(['load', 'play', 'pause']);
    expect(player.playing()).toBe(false);

    player.play();
    audio.pause(); // headphones unplugged, lock-screen button
    expect(events).toEqual(['load', 'play', 'pause', 'play', 'pause']);
    expect(player.playing()).toBe(false);
  });

  it('does not resume by itself when a paused book is moved to another track', () => {
    const { audio, player } = setup();
    audio.finishLoading();

    player.seek(300);
    audio.finishLoading();

    expect(audio.playCalls).toBe(0);
    expect(player.playing()).toBe(false);
  });

  it('keeps playing across a seek to another track', () => {
    const { audio, events, player } = setup();
    audio.finishLoading();
    player.play();

    player.seek(300);
    expect(events).toEqual(['load', 'play']);
    audio.finishLoading();

    expect(audio.playCalls).toBe(2);
    expect(audio.currentTime).toBe(200);
    expect(player.playing()).toBe(true);
  });

  it('starts playing once a track that was still loading is ready', () => {
    const { audio, events, player } = setup();

    player.play(); // pressed before the first track has loaded
    expect(audio.playCalls).toBe(0);

    audio.finishLoading();
    expect(audio.playCalls).toBe(1);
    expect(events).toEqual(['load', 'play']);
  });

  it('keeps the playback speed on every track', () => {
    const { audio, player } = setup();
    audio.finishLoading();
    player.rate(1.5);
    expect(audio.playbackRate).toBe(1.5);

    player.play();
    audio.runOut();
    audio.finishLoading();

    expect(audio.playbackRate).toBe(1.5);
  });

  it('reports a track that cannot be loaded and stops', () => {
    const { audio, events, player } = setup();
    audio.finishLoading();
    player.play();

    audio.failNextLoad = true;
    audio.runOut();
    audio.finishLoading();

    expect(events).toEqual(['load', 'play', 'pause', 'loaderror']);
    expect(player.playing()).toBe(false);
  });

  it('goes quiet after unload', () => {
    const { audio, events, player } = setup();
    audio.finishLoading();
    player.play();

    player.unload();
    audio.runOut();
    audio.finishLoading();

    expect(events).toEqual(['load', 'play']);
    expect(audio.src).toBe('');
  });

  it('reports a load error for a book with no tracks', async () => {
    const { events } = setup([]);
    await Promise.resolve();
    expect(events).toEqual(['loaderror']);
  });
});
