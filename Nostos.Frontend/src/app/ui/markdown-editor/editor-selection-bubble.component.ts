import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

import { IconButtonComponent } from '../icon-button/icon-button.component';

export type BlockKind = 'paragraph' | 'h1' | 'h2' | 'h3' | 'blockquote' | 'codeBlock';

/**
 * Selection-bubble toolbar and inline URL prompt (issue #1040).
 * TipTap's BubbleMenu owns attaching/detaching and positioning this host element.
 */
@Component({
  selector: 'app-editor-selection-bubble',
  standalone: true,
  imports: [IconButtonComponent],
  templateUrl: './editor-selection-bubble.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'editor-bubble',
    role: 'toolbar',
    'aria-label': 'Formatting',
    'data-testid': 'editor-bubble',
  },
})
export class EditorSelectionBubbleComponent {
  readonly activeBlock = input<BlockKind>('paragraph');
  readonly isBold = input<boolean>(false);
  readonly isItalic = input<boolean>(false);
  readonly isLink = input<boolean>(false);
  readonly urlPrompt = input<'link' | 'image' | null>(null);
  readonly urlValue = input<string>('');

  readonly toggleBold = output<void>();
  readonly toggleItalic = output<void>();
  readonly toggleBlock = output<BlockKind>();
  readonly addFootnote = output<void>();
  readonly openUrlPrompt = output<'link' | 'image'>();
  readonly urlValueChange = output<string>();
  readonly applyUrl = output<void>();
  readonly cancelUrl = output<void>();

  private readonly urlInput = viewChild<ElementRef<HTMLInputElement>>('urlInput');
  private readonly destroyRef = inject(DestroyRef);
  private focusTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect((onCleanup) => {
      const kind = this.urlPrompt();
      const inputEl = this.urlInput()?.nativeElement;
      this.clearFocusTimer();
      if (!kind || !inputEl) return;
      // The field is created by this change; focus it once it exists, or the
      // next keystrokes would land in (and overwrite) the selected text.
      this.focusTimer = setTimeout(() => {
        this.focusTimer = null;
        inputEl.focus();
      });
      onCleanup(() => this.clearFocusTimer());
    });

    this.destroyRef.onDestroy(() => this.clearFocusTimer());
  }

  /** Cancels any pending focus timer when the URL prompt or bubble closes. */
  clearFocusTimer(): void {
    if (this.focusTimer !== null) {
      clearTimeout(this.focusTimer);
      this.focusTimer = null;
    }
  }
}
