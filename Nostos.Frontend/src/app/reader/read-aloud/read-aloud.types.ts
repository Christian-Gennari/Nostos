import { Signal } from '@angular/core';

/** One spoken unit: a sentence, or a few short ones joined. */
export interface ReadAloudSegment {
  text: string;
}

/**
 * What a text reader offers so its book can be read aloud.
 *
 * The reader owns the position: it knows where the visible page starts, which
 * sentence comes next, how to show it and when a page must turn. The controller
 * only asks for the next thing to say.
 */
export interface ReaderReadAloud {
  /** The book's language tag when the file declares one. */
  language(): string | null;
  /**
   * Places the cursor on the first sentence of the visible page, marks it and
   * returns it. Null when nothing on this page or after it can be read.
   */
  begin(): Promise<ReadAloudSegment | null>;
  /**
   * Moves the cursor one sentence, marks it and brings it into view. Null at
   * the end (or start) of the book.
   */
  step(direction: 1 | -1): Promise<ReadAloudSegment | null>;
  /** Texts after the cursor that are already known, for a voice that prefetches. */
  upcoming(count: number): string[];
  /** Removes the mark and forgets the cursor. */
  end(): void;
  /** Bumped when the person moves the book themselves while it is being read. */
  userMoved: Signal<number>;
}

export type ReadAloudOutcome = 'ended' | 'cancelled' | 'failed';

export interface ReadAloudSpeakOptions {
  language: string | null;
  /** Playback speed, 1 = normal. */
  rate: number;
  /** Called once sound is actually coming out, for a voice that has to fetch it first. */
  onStart?: () => void;
}

/**
 * Something that can say one segment. A voice never queues: the controller
 * hands it one segment at a time and waits for the outcome.
 */
export interface ReadAloudVoice {
  readonly id: string;
  /** Resolves when the segment has been spoken, cancelled, or could not be spoken. */
  speak(text: string, options: ReadAloudSpeakOptions): Promise<ReadAloudOutcome>;
  /** Stops speaking now. The pending `speak` resolves 'cancelled'. */
  cancel(): void;
  /**
   * Called synchronously from the tap that starts or resumes reading, for a
   * voice that has to unlock audio playback inside a user gesture.
   */
  prime?(): void;
  /** Lets a voice that fetches audio get ahead of playback. */
  prepare?(texts: string[], options: ReadAloudSpeakOptions): void;
  /** Why the last `speak` failed, as a stable code, for choosing a fallback. */
  lastFailure?(): string | null;
}

export const READ_ALOUD_RATES = [0.75, 1, 1.25, 1.5, 1.75, 2] as const;
