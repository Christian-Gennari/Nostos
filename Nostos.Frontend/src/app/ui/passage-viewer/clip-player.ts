import { WritableSignal, signal } from '@angular/core';
import { Howl } from 'howler';

import { AudioPlayer, TrackPlaylistPlayer } from '../../reader/audio-reader/track-playlist-player';

/**
 * Announced when a clip starts playing, so the audio reader (if it is open
 * behind the viewer) pauses the book rather than talking over it.
 */
export const PASSAGE_CLIP_PLAY_EVENT = 'nostos-passage-clip-play';

/** How often the position is read while playing, in ms. */
const TICK_MS = 200;

/**
 * Plays one clip of an audiobook (issue #1019): from its start to its end and
 * no further. Built from the same players the audio reader uses, so a
 * multi-track book plays as one timeline here too. Positions are seconds
 * across the whole book.
 */
export class ClipPlayer {
  readonly ready = signal(false);
  readonly failed = signal(false);
  readonly playing = signal(false);
  readonly position: WritableSignal<number>;

  private readonly player: AudioPlayer;
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(
    book: { id: string; tracks?: Array<{ number: number; duration: number }> | null },
    readonly start: number,
    readonly end: number,
  ) {
    this.position = signal(start);
    const callbacks = {
      onload: () => {
        this.player.seek(start);
        this.ready.set(true);
      },
      onloaderror: () => this.failed.set(true),
      onplay: () => {
        this.playing.set(true);
        this.startTicking();
      },
      onpause: () => {
        this.playing.set(false);
        this.stopTicking();
      },
      onend: () => {
        // A clip that runs to the end of the book ends with the book.
        this.playing.set(false);
        this.stopTicking();
        this.position.set(this.end);
      },
    };

    const tracks = book.tracks ?? [];
    this.player =
      tracks.length > 0
        ? new TrackPlaylistPlayer(
            tracks.map((track) => ({
              url: `/api/books/${book.id}/tracks/${track.number}`,
              duration: track.duration,
            })),
            callbacks,
          )
        : (new Howl({
            src: [`/api/books/${book.id}/file`],
            html5: true,
            format: ['mp3', 'm4a', 'm4b'],
            ...callbacks,
          }) as unknown as AudioPlayer);
  }

  get length(): number {
    return this.end - this.start;
  }

  toggle(): void {
    if (this.playing()) {
      this.player.pause();
      return;
    }
    // At the end of the clip, play starts it again.
    if (this.position() >= this.end - 0.25) this.seek(this.start);
    window.dispatchEvent(new Event(PASSAGE_CLIP_PLAY_EVENT));
    this.player.play();
  }

  /** Moves within the clip; never outside it. */
  seek(seconds: number): void {
    const at = Math.min(this.end, Math.max(this.start, seconds));
    this.player.seek(at);
    this.position.set(at);
  }

  skip(delta: number): void {
    this.seek(this.position() + delta);
  }

  destroy(): void {
    this.stopTicking();
    this.player.unload();
  }

  private startTicking(): void {
    this.stopTicking();
    this.timer = setInterval(() => {
      const at = Number(this.player.seek());
      if (!Number.isFinite(at)) return;
      if (at >= this.end) {
        this.player.pause();
        this.position.set(this.end);
        return;
      }
      this.position.set(at);
    }, TICK_MS);
  }

  private stopTicking(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = null;
  }
}
