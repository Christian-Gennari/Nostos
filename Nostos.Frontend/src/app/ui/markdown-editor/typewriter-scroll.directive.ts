import { DestroyRef, Directive, ElementRef, effect, inject, input } from '@angular/core';
import type { Editor } from '@tiptap/core';

/**
 * Typewriter mode: keeps the caret line near 45% of the ProseMirror scroller
 * and preserves the caret's screen position when toggling the runway padding.
 */
@Directive({
  selector: '[appTypewriterScroll]',
  standalone: true,
})
export class TypewriterScrollDirective {
  readonly enabled = input<boolean>(false, { alias: 'appTypewriterScroll' });
  readonly editor = input<Editor | null>(null, { alias: 'typewriterEditor' });

  private readonly scroller = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private boundEditor: Editor | null = null;
  private readonly onEditorFollow = () => this.followCaret();

  constructor() {
    effect(() => {
      const on = this.enabled();
      this.toggleTypewriterLayout(on);
      if (on) queueMicrotask(() => this.followCaret());
    });

    effect(() => {
      this.bindEditor(this.editor());
    });

    this.destroyRef.onDestroy(() => this.bindEditor(null));
  }

  attachEditor(editor: Editor | null): void {
    this.bindEditor(editor);
  }

  followCaret(): void {
    const editor = this.boundEditor ?? this.editor();
    if (!this.enabled() || !editor || editor.isDestroyed) return;
    try {
      const scroller = this.scroller.nativeElement;
      const caret = editor.view.coordsAtPos(editor.state.selection.head);
      const rectTop = caret.top - scroller.getBoundingClientRect().top;
      const delta = caretScrollDelta(rectTop, scroller.clientHeight);
      if (delta !== null) scroller.scrollBy?.({ top: delta });
    } catch {
      // Caret geometry is best-effort — never break typing over it.
    }
  }

  private bindEditor(editor: Editor | null): void {
    if (editor === this.boundEditor) return;
    if (this.boundEditor) {
      this.boundEditor.off('update', this.onEditorFollow);
      this.boundEditor.off('selectionUpdate', this.onEditorFollow);
    }
    this.boundEditor = editor;
    if (editor) {
      editor.on('update', this.onEditorFollow);
      editor.on('selectionUpdate', this.onEditorFollow);
    }
  }

  /**
   * Typewriter mode adds scroll runway above the page. Keep the caret where it
   * is on screen while the runway appears or goes, so the toggle never jumps.
   */
  private toggleTypewriterLayout(on: boolean): void {
    const scroller = this.scroller?.nativeElement;
    if (!scroller) return;
    const before = this.caretTop();
    scroller.classList.toggle('typewriter', on);
    const after = this.caretTop();
    if (before !== null && after !== null && after !== before) {
      scroller.scrollBy?.({ top: after - before });
    }
  }

  private caretTop(): number | null {
    const editor = this.boundEditor ?? this.editor();
    if (!editor || editor.isDestroyed) return null;
    try {
      return editor.view.coordsAtPos(editor.state.selection.head).top;
    } catch {
      return null;
    }
  }
}

/**
 * Pixels to scroll so a caret at `rectTop` lands at 45% of `viewportHeight`,
 * or null inside a roughly one-line deadband / for invalid viewports.
 */
export function caretScrollDelta(rectTop: number, viewportHeight: number): number | null {
  if (!isFinite(rectTop) || !isFinite(viewportHeight) || viewportHeight <= 0) return null;
  const delta = rectTop - viewportHeight * 0.45;
  return Math.abs(delta) < 32 ? null : Math.round(delta);
}
