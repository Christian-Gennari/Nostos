import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import type { ReadAloudServerVoice } from '../../core/services/read-aloud.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';
import { PlaybackSpeedMenuComponent } from '../../ui/playback-speed-menu/playback-speed-menu.component';
import { ReadAloudController } from './read-aloud-controller';
import { READ_ALOUD_RATES } from './read-aloud.types';

/**
 * How long a sentence is waited on before the play button shows it. Most
 * arrive well inside this, already fetched ahead.
 */
export const READ_ALOUD_RING_DELAY_MS = 1000;

/** "Emma" for a voice whose accent the book's language already says; "Emma · British" otherwise. */
export function readAloudVoiceLabel(voice: ReadAloudServerVoice, all: readonly ReadAloudServerVoice[]): string {
  const accents = new Set(all.map((v) => v.language));
  if (accents.size < 2) return voice.name;
  // Every voice is offered when the book does not say its language: name the
  // language, not just the accent.
  const languages = new Set(all.map((v) => v.language.split('-')[0]));
  if (languages.size > 1) return `${voice.name} · ${languageName(voice.language)}`;
  const accent: Record<string, string> = { 'en-GB': 'British', 'en-US': 'American', 'pt-BR': 'Brazilian' };
  return accent[voice.language] ? `${voice.name} · ${accent[voice.language]}` : voice.name;
}

/**
 * The transport for read-aloud: shown only while a book is being read, floating
 * above the pager. It rests with the rest of the chrome, so a page put in
 * focus is only the page; the tap that brings the chrome back brings it too.
 *
 * The voice and the speed are chosen here, where they are heard, not in a
 * settings panel: the voice menu lists the natural voices this book's language
 * has, the device's own voice, and says why there are no natural ones when
 * there are not.
 */
@Component({
  selector: 'app-read-aloud-bar',
  standalone: true,
  imports: [ButtonComponent, IconButtonComponent, NostosIconComponent, PlaybackSpeedMenuComponent, ...NOSTOS_MENU],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './read-aloud-bar.component.html',
  styleUrl: './read-aloud-bar.component.css',
})
export class ReadAloudBarComponent {
  readonly controller = inject(ReadAloudController);

  /** Natural voices for the open book's language. Empty when there are none. */
  readonly voices = input<ReadAloudServerVoice[]>([]);
  /** A voice id, or 'device'. */
  readonly selectedVoice = input<string>('device');
  /** True when the host meters the natural voice (Cloud). */
  readonly managed = input(false);
  /** Why no natural voice is offered; null when one is. */
  readonly voiceNote = input<string | null>(null);
  /** True while the reader's chrome is put away: the transport fades out with it. */
  readonly resting = input(false);
  readonly voiceChange = output<string>();

  readonly rates = READ_ALOUD_RATES;

  /**
   * True once a sentence has been waited on for READ_ALOUD_RING_DELAY_MS: only
   * then does the ring turn around the play button. The ring is not drawn at
   * all before, so it cannot flash between sentences in any engine (a CSS
   * delay on the drawn ring still showed a frame of it on each one).
   */
  readonly waiting = signal(false);

  constructor() {
    effect((onCleanup) => {
      // Read first: every new wait restarts the count.
      const buffering = this.controller.buffering();
      this.waiting.set(false);
      if (!buffering) return;
      const timer = setTimeout(() => this.waiting.set(true), READ_ALOUD_RING_DELAY_MS);
      onCleanup(() => clearTimeout(timer));
    });
  }

  readonly toggleLabel = computed(() => (this.controller.playing() ? 'Pause reading' : 'Resume reading'));

  /** What is speaking now: the device voice after a fallback, whatever was chosen. */
  readonly voiceLabel = computed(() => {
    if (this.controller.activeVoiceId() === 'browser') return 'Device';
    const voice = this.voices().find((v) => v.id === this.selectedVoice());
    return voice ? voice.name : 'Device';
  });

  label(voice: ReadAloudServerVoice): string {
    return readAloudVoiceLabel(voice, this.voices());
  }
}

function languageName(tag: string): string {
  try {
    return new Intl.DisplayNames(['en'], { type: 'language' }).of(tag) ?? tag;
  } catch {
    return tag;
  }
}
