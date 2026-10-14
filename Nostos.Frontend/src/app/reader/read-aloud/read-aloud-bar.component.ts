import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import type { ReadAloudServerVoice } from '../../core/services/read-aloud.service';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { NOSTOS_MENU } from '../../ui/menu/menu.component';
import { ReadAloudController } from './read-aloud-controller';
import { READ_ALOUD_RATES } from './read-aloud.types';

/** "Emma" for a voice whose accent the book's language already says; "Emma · British" otherwise. */
export function readAloudVoiceLabel(voice: ReadAloudServerVoice, all: readonly ReadAloudServerVoice[]): string {
  const accents = new Set(all.map((v) => v.language));
  if (accents.size < 2) return voice.name;
  const accent: Record<string, string> = { 'en-GB': 'British', 'en-US': 'American', 'pt-BR': 'Brazilian' };
  return accent[voice.language] ? `${voice.name} · ${accent[voice.language]}` : voice.name;
}

/**
 * The transport for read-aloud: shown only while a book is being read, floating
 * above the pager so it stays in reach when the rest of the chrome hides.
 *
 * The voice and the speed are chosen here, where they are heard, not in a
 * settings panel: the voice menu lists the natural voices this book's language
 * has, the device's own voice, and says why there are no natural ones when
 * there are not.
 */
@Component({
  selector: 'app-read-aloud-bar',
  standalone: true,
  imports: [IconButtonComponent, NostosIconComponent, ...NOSTOS_MENU],
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
  readonly voiceChange = output<string>();

  readonly rates = READ_ALOUD_RATES;

  readonly rateLabel = computed(() => formatRate(this.controller.rate()));
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

  formatRate(rate: number): string {
    return formatRate(rate);
  }
}

function formatRate(rate: number): string {
  return `${rate}×`;
}
