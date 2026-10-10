/**
 * What the audio reader needs from whatever is producing sound. Howler's
 * `Howl` already has this shape for a single-file audiobook;
 * {@link TrackPlaylistPlayer} provides it for a multi-track one. Positions and
 * the duration are always seconds across the WHOLE book.
 */
export interface AudioPlayer {
  play(): unknown;
  pause(): unknown;
  playing(): boolean;
  seek(): unknown;
  seek(seconds: number): unknown;
  rate(rate: number): unknown;
  duration(): number;
  unload(): void;
}

export interface PlaylistTrack {
  url: string;
  /** Recorded length in seconds. */
  duration: number;
}

export interface PlaylistCallbacks {
  onload: () => void;
  onloaderror: () => void;
  onplay: () => void;
  onpause: () => void;
  onend: () => void;
}

/**
 * Plays an ordered list of audio files as one continuous book.
 *
 * The book is a virtual timeline: track N starts where the recorded lengths of
 * the tracks before it end. Everything outside this class (progress, chapters,
 * the scrubber, sleep-at-chapter-end, Media Session position) keeps working in
 * book seconds and never learns there are several files.
 *
 * ONE audio element is reused for every track. Starting playback on a fresh
 * element needs a user gesture, and a locked phone grants none, so a new
 * element per track would stop the book at the first track boundary. Swapping
 * the source of the element that is already playing does not.
 *
 * Track boundaries are driven by the element's own `ended` event, never by the
 * recorded lengths. A recorded length that is a few milliseconds off therefore
 * costs a tiny step in the displayed time and can never cut a track short or
 * leave silence at its end.
 */
export class TrackPlaylistPlayer implements AudioPlayer {
  private readonly audio: HTMLAudioElement;
  private readonly starts: number[] = [];
  private readonly total: number;
  private index = 0;
  /** Offset in the current track to apply once its metadata has loaded. */
  private pendingOffset: number | null = null;
  private wantPlaying = false;
  private reportedPlaying = false;
  private switching = false;
  private loadedOnce = false;
  private disposed = false;
  private playbackRate = 1;

  private readonly onLoadedMetadata = () => this.handleLoadedMetadata();
  private readonly onPlaying = () => this.handlePlaying();
  private readonly onPause = () => this.handlePause();
  private readonly onEnded = () => this.handleEnded();
  private readonly onError = () => this.handleError();

  constructor(
    private readonly tracks: PlaylistTrack[],
    private readonly callbacks: PlaylistCallbacks,
    audioFactory: () => HTMLAudioElement = () => new Audio(),
  ) {
    let cursor = 0;
    for (const track of tracks) {
      this.starts.push(cursor);
      cursor += Math.max(0, track.duration);
    }
    this.total = cursor;

    this.audio = audioFactory();
    this.audio.preload = 'metadata';
    this.audio.addEventListener('loadedmetadata', this.onLoadedMetadata);
    this.audio.addEventListener('playing', this.onPlaying);
    this.audio.addEventListener('pause', this.onPause);
    this.audio.addEventListener('ended', this.onEnded);
    this.audio.addEventListener('error', this.onError);

    if (tracks.length === 0) {
      queueMicrotask(() => !this.disposed && this.callbacks.onloaderror());
    } else {
      this.load(0, 0);
    }
  }

  duration(): number {
    return this.total;
  }

  playing(): boolean {
    return this.wantPlaying;
  }

  /** The 0-based track currently loaded. */
  trackIndex(): number {
    return this.index;
  }

  seek(): number;
  seek(seconds: number): this;
  seek(seconds?: number): number | this {
    if (seconds === undefined) return this.position();

    const target = Math.max(0, Math.min(seconds, this.total));
    const index = this.indexAt(target);
    const offset = Math.max(0, target - this.starts[index]);

    if (index !== this.index || this.pendingOffset !== null) {
      this.load(index, offset);
    } else {
      this.setCurrentTime(offset);
    }
    return this;
  }

  play(): this {
    this.wantPlaying = true;
    // While a track is still loading, playback starts when its metadata lands.
    if (this.pendingOffset === null) this.startElement();
    return this;
  }

  pause(): this {
    this.wantPlaying = false;
    this.audio.pause();
    // A pause requested mid-switch produces no element event to report it.
    if (this.reportedPlaying && (this.switching || this.audio.paused)) this.reportPaused();
    return this;
  }

  rate(rate: number): this {
    this.playbackRate = rate;
    // A new source resets playbackRate to the default, so both are set.
    this.audio.defaultPlaybackRate = rate;
    this.audio.playbackRate = rate;
    return this;
  }

  unload(): void {
    this.disposed = true;
    this.wantPlaying = false;
    this.audio.removeEventListener('loadedmetadata', this.onLoadedMetadata);
    this.audio.removeEventListener('playing', this.onPlaying);
    this.audio.removeEventListener('pause', this.onPause);
    this.audio.removeEventListener('ended', this.onEnded);
    this.audio.removeEventListener('error', this.onError);
    this.audio.pause();
    this.audio.removeAttribute('src');
    this.audio.load();
  }

  private position(): number {
    if (this.pendingOffset !== null) return this.starts[this.index] + this.pendingOffset;
    // Clamped to the recorded length so the book position never runs past the
    // next track's start while this one plays out its last few milliseconds.
    const within = Math.min(this.audio.currentTime || 0, this.tracks[this.index]?.duration ?? 0);
    return this.starts[this.index] + within;
  }

  private indexAt(seconds: number): number {
    for (let i = this.tracks.length - 1; i > 0; i--) {
      if (seconds >= this.starts[i]) return i;
    }
    return 0;
  }

  private load(index: number, offset: number): void {
    this.switching = true;
    this.index = index;
    this.pendingOffset = offset;
    this.audio.src = this.tracks[index].url;
    this.audio.load();
  }

  private handleLoadedMetadata(): void {
    if (this.disposed) return;
    const offset = this.pendingOffset ?? 0;
    this.pendingOffset = null;
    this.switching = false;
    this.audio.defaultPlaybackRate = this.playbackRate;
    this.audio.playbackRate = this.playbackRate;
    if (offset > 0) this.setCurrentTime(offset);

    if (!this.loadedOnce) {
      this.loadedOnce = true;
      this.callbacks.onload();
    }
    if (this.wantPlaying) this.startElement();
  }

  private handlePlaying(): void {
    if (this.disposed || this.reportedPlaying) return;
    this.wantPlaying = true;
    this.reportedPlaying = true;
    this.callbacks.onplay();
  }

  private handlePause(): void {
    // The element also pauses when a track runs out and while its source is
    // being swapped. Neither is the listener pausing the book.
    if (this.disposed || this.switching || this.audio.ended) return;
    this.wantPlaying = false;
    this.reportPaused();
  }

  private handleEnded(): void {
    if (this.disposed) return;
    if (this.index < this.tracks.length - 1) {
      // Still inside the event the element raised, so the next track may
      // start without a new user gesture.
      this.load(this.index + 1, 0);
      return;
    }

    this.wantPlaying = false;
    this.reportedPlaying = false;
    this.callbacks.onend();
  }

  private handleError(): void {
    if (this.disposed) return;
    this.switching = false;
    this.pendingOffset = null;
    const wasPlaying = this.reportedPlaying;
    this.wantPlaying = false;
    if (wasPlaying) this.reportPaused();
    this.callbacks.onloaderror();
  }

  private reportPaused(): void {
    if (!this.reportedPlaying) return;
    this.reportedPlaying = false;
    this.callbacks.onpause();
  }

  private startElement(): void {
    const started = this.audio.play();
    // A rejected play() (autoplay policy, or a source swapped underneath it)
    // must not surface as an unhandled rejection.
    if (started && typeof started.catch === 'function') {
      started.catch(() => {
        if (this.disposed || this.switching) return;
        this.wantPlaying = false;
        this.reportPaused();
      });
    }
  }

  private setCurrentTime(offset: number): void {
    try {
      this.audio.currentTime = offset;
    } catch {
      // Not seekable yet; the next loadedmetadata applies the pending offset.
      this.pendingOffset = offset;
    }
  }
}
