/**
 * The composer: draft field, source/book chip, voice controls, send and the
 * pre-dispatch Undo for a transcribed capture.
 *
 * It owns its field geometry and focus, and every deferred focus/size pass is
 * an `afterNextRender` callback tied to this component's injector, so teardown
 * cannot leave a timer pointing at a removed textarea. Sending is delegated to
 * the service; the shell only learns that a send happened so it can follow the
 * reply.
 */
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  output,
  viewChild,
} from '@angular/core';

import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { formatTimestamp } from './assistant.service';
import { AssistantService } from './assistant.service';
import { AssistantVoiceService } from './assistant-voice.service';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';

@Component({
  selector: 'app-assistant-composer',
  standalone: true,
  imports: [ButtonComponent, NostosIconComponent],
  templateUrl: './assistant-composer.component.html',
  styleUrl: './assistant-composer.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'assistant-composer' },
})
export class AssistantComposerComponent {
  readonly assistant = inject(AssistantService);
  readonly voice = inject(AssistantVoiceService);
  private readonly preferences = inject(LibraryPreferencesService);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly composer = viewChild<ElementRef<HTMLTextAreaElement>>('composer');

  /** A message was submitted; the shell returns the transcript to its end. */
  readonly submitted = output<void>();

  /** Product-level voice preference; provider configuration stays server-side. */
  readonly voiceTranscriptionEnabled = this.preferences.assistantVoiceEnabled;

  /** Drives the quiet send affordance without making the template inspect text. */
  readonly hasDraft = computed(() => this.assistant.draft().trim().length > 0);

  /** The book the composer is about, unless a source-location chip already names it. */
  readonly readingLabel = computed(() => {
    if (this.assistant.anchorChip()) return null;
    const title = this.assistant.context().bookTitle?.trim();
    return title ? title : null;
  });

  constructor() {
    // A finished transcript enters here and nowhere else. The conversation owns
    // the one send policy; the composer keeps focus so the words can be read.
    this.voice.onTranscript = (text) => {
      this.assistant.insertTranscript(text);
      this.focusAfterRender();
    };
    this.destroyRef.onDestroy(() => {
      this.voice.onTranscript = null;
    });
  }

  /** Focus the field; used by the shell after opening. */
  focus(): void {
    this.composer()?.nativeElement.focus();
  }

  /** One deferred focus pass, owned by this component and cleared on destroy. */
  private focusAfterRender(): void {
    afterNextRender(() => this.focus(), { injector: this.injector });
  }

  elapsedLabel(): string {
    return formatTimestamp(String(this.voice.elapsedSeconds()));
  }

  onMicTap(): void {
    if (!this.voiceTranscriptionEnabled()) return;
    if (this.voice.isRecording()) {
      this.voice.stop();
      return;
    }
    if (this.voice.status() === 'idle') this.voice.start();
  }

  onVoiceStop(): void {
    this.voice.stop();
  }

  onVoiceCancel(): void {
    this.voice.cancel();
  }

  /** Pre-dispatch Undo: keeps the transcript in the composer, sends nothing. */
  onUndoTranscript(): void {
    this.assistant.undoTranscript();
    this.focusAfterRender();
  }

  onComposerInput(event: Event): void {
    const element = event.target as HTMLTextAreaElement;
    this.assistant.updateDraft(element.value);
    this.autoGrow(element);
  }

  onComposerEnter(event: Event): void {
    const keyboard = event as KeyboardEvent;
    if (keyboard.shiftKey) return; // Shift+Enter is a newline.
    event.preventDefault();
    this.submitDraft();
  }

  onSendClick(): void {
    if (!this.hasDraft() || this.assistant.sending()) return;
    this.submitDraft();
    this.focusAfterRender();
  }

  /**
   * Puts a door's example in the composer and leaves it there. It never
   * submits: a prefilled write is shown to the person before it can run.
   */
  prefill(text: string): void {
    if (this.assistant.sending()) return;
    this.assistant.updateDraft(text);
    afterNextRender(() => {
      const element = this.composer()?.nativeElement;
      if (!element) return;
      element.value = this.assistant.draft();
      this.autoGrow(element);
      element.focus();
      element.setSelectionRange(element.value.length, element.value.length);
    }, { injector: this.injector });
  }

  private submitDraft(): void {
    this.assistant.submit();
    this.submitted.emit();
    const element = this.composer()?.nativeElement;
    if (element) {
      element.value = this.assistant.draft();
      this.autoGrow(element);
    }
  }

  private autoGrow(element: HTMLTextAreaElement): void {
    element.style.height = 'auto';
    element.style.height = `${Math.min(element.scrollHeight, 160)}px`;
  }
}
