import { DestroyRef, Directive, ElementRef, effect, inject, input } from '@angular/core';

import { SoundPack, TypewriterSounds, soundForInput } from './typewriter-sounds';

/**
 * Attaches keystroke sound listeners to the ProseMirror editor/scroller and
 * manages the lazy `TypewriterSounds` sample lifecycle (issues #1042, #1065).
 */
@Directive({
  selector: '[appTypewriterSound]',
  standalone: true,
})
export class TypewriterSoundDirective {
  readonly enabled = input<boolean>(false, { alias: 'appTypewriterSound' });
  readonly volume = input<number>(0.6, { alias: 'typewriterSoundVolume' });
  readonly pack = input<SoundPack>('typewriter', { alias: 'typewriterSoundPack' });
  readonly target = input<HTMLElement | null>(null, { alias: 'typewriterSoundTarget' });
  readonly suppressReturn = input<boolean>(false, { alias: 'typewriterSuppressReturn' });

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  private typewriterSounds: TypewriterSounds | null = null;
  /** Whether the key behind the coming edit is auto-repeating (no sound). */
  private keyRepeating = false;
  /** When a keydown last made the sound itself, so its beforeinput does not again. */
  private keydownSoundAt = 0;
  private boundTarget: HTMLElement | null = null;
  private explicitTarget: HTMLElement | null = null;

  constructor() {
    // Turning sounds on fetches the samples (nothing is loaded before that);
    // turning them off releases the audio context.
    effect(() => {
      if (this.enabled()) {
        this.soundPlayer().setPack(this.pack());
        void this.soundPlayer().prefetch();
      } else if (this.typewriterSounds) {
        this.typewriterSounds.dispose();
        this.typewriterSounds = null;
      }
    });

    effect(() => {
      this.bindTarget(this.target() ?? this.explicitTarget ?? this.host.nativeElement);
    });

    this.destroyRef.onDestroy(() => {
      this.bindTarget(null);
      this.typewriterSounds?.dispose();
      this.typewriterSounds = null;
    });
  }

  attachTarget(target: HTMLElement | null): void {
    this.explicitTarget = target;
    this.bindTarget(target ?? this.target() ?? this.host.nativeElement);
  }

  private bindTarget(target: HTMLElement | null): void {
    if (target === this.boundTarget) return;
    if (this.boundTarget) {
      this.boundTarget.removeEventListener('keydown', this.onSoundKeydown);
      this.boundTarget.removeEventListener('keyup', this.onSoundKeyup);
      this.boundTarget.removeEventListener('beforeinput', this.onSoundInput);
    }
    this.boundTarget = target;
    if (target) {
      target.addEventListener('keydown', this.onSoundKeydown);
      target.addEventListener('keyup', this.onSoundKeyup);
      target.addEventListener('beforeinput', this.onSoundInput);
    }
  }

  private soundPlayer(): TypewriterSounds {
    this.typewriterSounds ??= new TypewriterSounds();
    return this.typewriterSounds;
  }

  private readonly onSoundKeyup = () => {
    this.keyRepeating = false;
  };

  private readonly onSoundKeydown = (event: KeyboardEvent) => {
    this.keyRepeating = event.repeat;
    // ProseMirror takes Enter and Backspace on keydown and cancels them, so no
    // beforeinput follows on a hardware keyboard: their sounds start here.
    if (!this.enabled() || event.repeat || event.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
    const sound = event.key === 'Enter' ? 'return' : event.key === 'Backspace' ? 'backspace' : null;
    if (!sound) return;
    // Enter picking from the @ palette or the / menu changes no text: no carriage.
    if (sound === 'return' && this.suppressReturn()) return;
    this.soundPlayer().play(sound, this.volume());
    this.keydownSoundAt = performance.now();
  };

  /**
   * Sounds follow what the keyboard did to the text (beforeinput), not key
   * codes: Android soft keyboards report every key as "Unidentified". Edits
   * the Studio makes itself (an inserted quote) fire no beforeinput, and an
   * IME composition in progress makes no sound until it is committed.
   */
  private readonly onSoundInput = (event: InputEvent) => {
    if (!this.enabled() || event.isComposing || this.keyRepeating) return;
    const sound = soundForInput(event.inputType, event.data);
    if (!sound) return;
    if ((sound === 'return' || sound === 'backspace') && performance.now() - this.keydownSoundAt < 100) return;
    this.soundPlayer().play(sound, this.volume(), event.data?.charCodeAt(0) ?? 0);
  };
}
