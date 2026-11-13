import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output } from '@angular/core';
import { Editor, Range } from '@tiptap/core';
import { TextSelection } from '@tiptap/pm/state';
import { exitSuggestion } from '@tiptap/suggestion';

import { markdownToHtml } from './markdown-conversion';
import {
  SOURCE_SUGGESTION_KEY,
  SOURCE_SUGGESTION_PREFIXES,
  SourceSuggestionHandlers,
} from './source-suggestion.extension';

/** What has been typed after `@`, and where on screen (issue #1039). */
export interface SourceSuggestionState {
  query: string;
  rect: { top: number; bottom: number; left: number; right: number } | null;
}

/**
 * Coordinates the editor's `@` source-suggestion bridge (issue #1039):
 * TipTap suggestion state, keyboard shortcut `@` insertion/cleanup, held-query
 * tracking across viewer trips, and combobox ARIA attributes on the page DOM.
 */
@Component({
  selector: 'app-editor-source-suggestion',
  standalone: true,
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    hidden: '',
  },
})
export class EditorSourceSuggestionComponent {
  readonly editor = input<Editor | null>(null);
  readonly editorDom = input<HTMLElement | null>(null);
  readonly activeId = input<string | null>(null);
  readonly keyDownHandler = input<((event: KeyboardEvent) => boolean) | null>(null);

  readonly suggestionChange = output<SourceSuggestionState | null>();
  readonly openChange = output<boolean>();

  private readonly destroyRef = inject(DestroyRef);
  private destroyed = false;
  private attachedEditor: Editor | null = null;
  /** The text of the open `@` query (the `@` included), or null. */
  private suggestionRange: Range | null = null;
  private suggestionQuery = '';
  /** How many characters the shortcut typed (`@` or ` @`), or 0. */
  private shortcutInserted = 0;
  private heldQuery: { from: number; to: number; text: string } | null = null;

  constructor() {
    // The page is the palette's combobox: it owns focus, and the list is
    // announced through these while an `@` query is open.
    effect(() => {
      const activeId = this.activeId();
      const dom = this.editorDom();
      if (!dom) return;
      if (activeId) {
        dom.setAttribute('aria-activedescendant', activeId);
        dom.setAttribute('aria-controls', 'source-palette-list');
        dom.setAttribute('aria-expanded', 'true');
      } else {
        dom.removeAttribute('aria-activedescendant');
        dom.removeAttribute('aria-controls');
        dom.setAttribute('aria-expanded', 'false');
      }
    });

    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.attachedEditor = null;
    });
  }

  attachEditor(editor: Editor | null): void {
    this.attachedEditor = editor;
  }

  isOpen(): boolean {
    return this.suggestionRange !== null;
  }

  extensionOptions(): SourceSuggestionHandlers {
    return {
      onChange: (state) => {
        if (this.destroyed) return;
        if (!state) this.dropShortcutAt();
        this.suggestionRange = state?.range ?? null;
        this.suggestionQuery = state?.query ?? '';
        this.openChange.emit(this.suggestionRange !== null);
        this.suggestionChange.emit(
          state ? { query: state.query, rect: state.rect ? plainRect(state.rect) : null } : null,
        );
      },
      onKeyDown: (event) => (this.destroyed ? false : (this.keyDownHandler()?.(event) ?? false)),
      onOpenShortcut: () => (this.destroyed ? false : this.openQuery()),
    };
  }

  canReplace(snippet: string): boolean {
    const editor = this.currentEditor();
    return !!editor && !editor.isDestroyed && !!this.suggestionRange && !!snippet.trim();
  }

  replaceQuery(snippet: string, placement: 'block' | 'inline', insertBlock: (html: string) => void): boolean {
    const editor = this.currentEditor();
    const range = this.suggestionRange;
    if (!editor || editor.isDestroyed || !range) return false;
    this.shortcutInserted = 0;
    editor.chain().focus().deleteRange(range).run();
    this.suggestionRange = null;
    this.openChange.emit(false);
    if (placement === 'inline') {
      // Unwrap the paragraph Markdown puts around a lone line, so the
      // reference joins the sentence instead of starting a new one.
      const inline = markdownToHtml(snippet).trim().replace(/^<p>([\s\S]*)<\/p>$/, '$1');
      editor.chain().focus().insertContent(inline).run();
      // The reference is italic; the sentence that goes on after it is not.
      editor.view.dispatch(editor.state.tr.setStoredMarks([]));
    } else {
      insertBlock(markdownToHtml(snippet));
    }
    return true;
  }

  /** Close the open `@` query and leave its text as typed. */
  closeQuery(): void {
    const editor = this.currentEditor();
    if (!editor || editor.isDestroyed || !this.suggestionRange) return;
    exitSuggestion(editor.view, SOURCE_SUGGESTION_KEY);
  }

  /** Remove the open `@` query and leave the caret where it was typed. */
  clearQuery(): boolean {
    const editor = this.currentEditor();
    const range = this.suggestionRange;
    if (!editor || editor.isDestroyed || !range) return false;
    this.shortcutInserted = 0;
    editor.chain().deleteRange(range).run();
    this.suggestionRange = null;
    this.openChange.emit(false);
    return true;
  }

  /**
   * Close the open `@` query but remember what it was, for a choice that is
   * finished somewhere else (a passage read in the viewer). The typed words
   * stay on the page until the choice lands, so closing the viewer without
   * quoting loses nothing.
   */
  holdQuery(): boolean {
    const editor = this.currentEditor();
    const range = this.suggestionRange;
    if (!editor || editor.isDestroyed || !range) return false;
    this.heldQuery = { from: range.from, to: range.to, text: editor.state.doc.textBetween(range.from, range.to, ' ', ' ') };
    exitSuggestion(editor.view, SOURCE_SUGGESTION_KEY);
    return true;
  }

  /** Remove the held `@` query, if the page still holds exactly those words there. */
  releaseHeldQuery(): void {
    const editor = this.currentEditor();
    const held = this.heldQuery;
    this.heldQuery = null;
    if (!editor || editor.isDestroyed || !held) return;
    const size = editor.state.doc.content.size;
    if (held.to > size || editor.state.doc.textBetween(held.from, held.to, ' ', ' ') !== held.text) return;
    editor.chain().deleteRange({ from: held.from, to: held.to }).run();
  }

  /** Start an `@` query at the caret, as the keyboard shortcut does. */
  openQuery(): boolean {
    const editor = this.currentEditor();
    if (!editor || editor.isDestroyed) return false;
    const { selection } = editor.state;
    if (selection.$from.parent.type.spec.code) return false;
    // Never replace a selection: the query starts after it, in the nearest
    // text (a whole-document selection ends outside any paragraph).
    const end = TextSelection.near(editor.state.doc.resolve(selection.to), -1);
    const $at = end.$to;
    if (!$at.parent.isTextblock) return false;
    const before = $at.parent.textBetween(0, $at.parentOffset, ' ', ' ');
    const last = before.slice(-1);
    const needsSpace = last !== '' && !/\s/.test(last) && !SOURCE_SUGGESTION_PREFIXES.includes(last);
    const text = needsSpace ? ' @' : '@';
    editor.chain().focus().setTextSelection($at.pos).insertContent(text).run();
    this.shortcutInserted = text.length;
    return true;
  }

  private currentEditor(): Editor | null {
    return this.editor() ?? this.attachedEditor;
  }

  /**
   * A palette opened with the shortcut and closed with nothing typed leaves
   * no stray `@`: the shortcut typed it, so closing takes it back.
   */
  private dropShortcutAt(): void {
    const inserted = this.shortcutInserted;
    this.shortcutInserted = 0;
    const editor = this.currentEditor();
    const range = this.suggestionRange;
    if (!inserted || !editor || !range || this.suggestionQuery !== '') return;
    const from = range.to - inserted;
    const typed = inserted === 2 ? ' @' : '@';
    queueMicrotask(() => {
      if (editor.isDestroyed || editor.state.doc.textBetween(from, range.to) !== typed) return;
      editor.chain().deleteRange({ from, to: range.to }).run();
    });
  }
}

function plainRect(rect: DOMRect): SourceSuggestionState['rect'] {
  return { top: rect.top, bottom: rect.bottom, left: rect.left, right: rect.right };
}
