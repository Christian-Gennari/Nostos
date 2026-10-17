import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  ViewEncapsulation,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { Editor, Range, createDocument } from '@tiptap/core';
import { EditorState, TextSelection } from '@tiptap/pm/state';
import StarterKit from '@tiptap/starter-kit';
import Placeholder from '@tiptap/extension-placeholder';
import Typography from '@tiptap/extension-typography';
import Image from '@tiptap/extension-image';
import { TableKit } from '@tiptap/extension-table';
import BubbleMenu from '@tiptap/extension-bubble-menu';

import { IconButtonComponent } from '../icon-button/icon-button.component';
import { htmlToMarkdown, markdownToHtml } from './markdown-conversion';
import { SOURCE_SUGGESTION_KEY, SOURCE_SUGGESTION_PREFIXES, SourceSuggestion } from './source-suggestion.extension';
import { exitSuggestion } from '@tiptap/suggestion';

export interface MarkdownEditorTransientState {
  /** The selection, as ProseMirror document positions. */
  bookmark?: unknown;
  scrollY?: number;
}

/** What has been typed after `@`, and where on screen (issue #1039). */
export interface SourceSuggestionState {
  query: string;
  rect: { top: number; bottom: number; left: number; right: number } | null;
}

interface PendingTransientRestore {
  state: MarkdownEditorTransientState;
  expectedMarkdown: string;
  resolve: (restored: boolean) => void;
}

type BlockKind = 'paragraph' | 'h1' | 'h2' | 'h3' | 'blockquote' | 'codeBlock';

/**
 * The Studio's page: a TipTap editor that reads and writes Markdown
 * (issue #1037). It replaced TinyMCE behind the same contract, so the Studio,
 * autosave and source insertion did not change:
 *
 * - `initialContent` in and `contentChange` out are Markdown;
 * - `insertMarkdown()` inserts at the real selection;
 * - `captureTransientState()` / `restoreTransientState()` keep the caret and
 *   scroll across a trip to a source;
 * - `typewriter` keeps the caret line near 45% of the page.
 *
 * The editor lives in the app's own DOM (TinyMCE used an iframe), so the page
 * follows the theme through the `--manuscript-*` tokens with no sync step.
 *
 * Only formatting that survives a save is offered: Markdown has no underline,
 * so there is none. Tables load, edit and save as GFM pipe tables.
 */
@Component({
  selector: 'app-markdown-editor',
  standalone: true,
  imports: [IconButtonComponent],
  templateUrl: './markdown-editor.component.html',
  styleUrl: './markdown-editor.component.css',
  // ProseMirror renders the page's nodes itself, so emulated encapsulation
  // could never reach them. Every rule is scoped under `.nostos-manuscript`
  // or `.editor-toolbar` instead.
  encapsulation: ViewEncapsulation.None,
})
export class MarkdownEditorComponent implements AfterViewInit, OnDestroy {
  initialContent = input<string>('');
  contentChange = output<string>();
  wordCountChange = output<number>();
  /** Characters on the page, spaces included, for the Studio's stat corner. */
  characterCountChange = output<number>();
  /**
   * Typewriter mode: keep the caret line near 45% of the page by scrolling on
   * caret activity. Owned by the studio (toggle + persistence); the editor
   * only follows while this is true.
   */
  typewriter = input<boolean>(false);
  /** The `@` query while one is open, and null when it closes. */
  sourceSuggestion = output<SourceSuggestionState | null>();
  /** Keys pressed while an `@` query is open go here first; true consumes one. */
  sourceSuggestionKeyDown = input<((event: KeyboardEvent) => boolean) | null>(null);
  /** The palette's highlighted option, announced through the page's ARIA. */
  sourceSuggestionActiveId = input<string | null>(null);

  @ViewChild('mount', { static: true }) private mount!: ElementRef<HTMLElement>;
  @ViewChild('scroller', { static: true }) private scroller!: ElementRef<HTMLElement>;
  @ViewChild('urlInput') private urlInput?: ElementRef<HTMLInputElement>;
  @ViewChild('bubble', { static: true }) private bubble!: ElementRef<HTMLElement>;

  /** The editor's contenteditable element, once it exists. */
  private readonly editorDom = signal<HTMLElement | null>(null);

  /** True until the editor exists, so the empty page never flashes a caret. */
  readonly editorStarting = signal(true);

  /** Selection-bubble state, refreshed on every transaction. */
  readonly activeBlock = signal<BlockKind>('paragraph');
  readonly isBold = signal(false);
  readonly isItalic = signal(false);
  readonly isBulletList = signal(false);
  readonly isOrderedList = signal(false);
  readonly isLink = signal(false);
  readonly canUndo = signal(false);
  readonly canRedo = signal(false);

  /** The inline URL field the link and image buttons open. */
  readonly urlPrompt = signal<'link' | 'image' | null>(null);
  readonly urlValue = signal('');

  private editor: Editor | null = null;
  /** The last Markdown this editor emitted, so a save echo costs nothing. */
  private lastEmittedMarkdown: string | null = null;
  private destroyed = false;
  private lastExternalMarkdownApplied: string | null = null;
  /** The text of the open `@` query (the `@` included), or null. */
  private suggestionRange: Range | null = null;
  private suggestionQuery = '';
  /** How many characters the shortcut typed (`@` or ` @`), or 0. */
  private shortcutInserted = 0;
  private pendingTransientRestore: PendingTransientRestore | null = null;

  constructor() {
    // The page is the palette's combobox: it owns focus, and the list is
    // announced through these while an `@` query is open.
    effect(() => {
      const activeId = this.sourceSuggestionActiveId();
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

    effect(() => {
      const on = this.typewriter();
      this.toggleTypewriterLayout(on);
      if (on) queueMicrotask(() => this.followCaret());
    });

    // External content (opening another document) replaces the page only when
    // it really differs, so a save echo never moves the caret.
    effect(() => {
      const markdown = this.initialContent();
    // The bubble plugin attaches its element when a selection appears and
    // removes it when it goes; rendered in place it would sit on the page as
    // a bar. Angular keeps updating its bindings while it is detached.
    this.bubble.nativeElement.remove();
      const editor = this.editor;
      if (
        editor &&
        markdown !== this.lastEmittedMarkdown &&
        htmlToMarkdown(editor.getHTML()).trim() !== markdown.trim()
      ) {
        this.replaceDocument(editor, markdown);
      }
      this.lastExternalMarkdownApplied = markdown;
      this.tryApplyTransientRestore();
    });
  }

  ngAfterViewInit(): void {
    if (this.destroyed) return;
    const markdown = this.initialContent();

    this.editor = new Editor({
      element: this.mount.nativeElement,
      extensions: [
        StarterKit.configure({
          // The toolbar offers H1-H3, but a writing may already hold deeper
          // headings; the schema keeps them rather than flattening them.
          heading: { levels: [1, 2, 3, 4, 5, 6] },
          // Markdown cannot keep underline; offering it lost it on every save.
          underline: false,
          link: { openOnClick: false, autolink: true, defaultProtocol: 'https' },
        }),
        Placeholder.configure({ placeholder: 'Start writing…' }),
        Typography,
        // Writings saved under TinyMCE can hold pasted images as data URIs.
        Image.configure({ allowBase64: true }),
        TableKit.configure({ table: { resizable: false } }),
        BubbleMenu.configure({
          element: this.bubble.nativeElement,
          // Fixed to the viewport, following the page's own scroller: inside
          // that scroller an absolutely placed bubble resolved against the
          // wrong offset parent and landed in the corner, under the header.
          options: {
            strategy: 'fixed',
            placement: coarsePointer() ? 'bottom' : 'top',
            offset: 8,
            flip: true,
            shift: { padding: 8 },
            scrollTarget: this.scroller.nativeElement,
          },
          // Never over an open @ query, an image or a code block.
          shouldShow: ({ editor, from, to, element }) => {
            if (element.contains(document.activeElement)) return true;
            if (from === to || !editor.isFocused || this.suggestionRange) return false;
            return !editor.isActive('codeBlock') && !editor.isActive('image');
          },
        }),
        SourceSuggestion.configure({
          onChange: (state) => {
            if (!state) this.dropShortcutAt();
            this.suggestionRange = state?.range ?? null;
            this.suggestionQuery = state?.query ?? '';
            this.sourceSuggestion.emit(
              state ? { query: state.query, rect: state.rect ? plainRect(state.rect) : null } : null,
            );
          },
          onKeyDown: (event) => this.sourceSuggestionKeyDown()?.(event) ?? false,
          onOpenShortcut: () => this.openSourceQuery(),
        }),
      ],
      content: markdownToHtml(markdown),
      editorProps: {
        attributes: {
          class: 'nostos-manuscript-body',
          spellcheck: 'true',
          'aria-label': 'Document',
          'aria-multiline': 'true',
          role: 'textbox',
        },
        handlePaste: (_view, event) => this.pasteMarkdown(event),
      },
      onUpdate: () => {
        this.emitMarkdown();
        this.emitWordCount();
        this.followCaret();
      },
      onSelectionUpdate: () => this.followCaret(),
      onTransaction: () => this.refreshToolbar(),
    });

    this.lastExternalMarkdownApplied ??= markdown;
    this.editorDom.set(this.editor.view.dom as HTMLElement);
    this.editorStarting.set(false);
    this.refreshToolbar();
    this.emitWordCount();
    this.tryApplyTransientRestore();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.pendingTransientRestore?.resolve(false);
    this.pendingTransientRestore = null;
    this.bubble?.nativeElement.remove();
    if (this.editor) {
      // The last keystrokes may still be inside the debounce window upstream.
      this.emitMarkdown();
      this.editor.destroy();
      this.editor = null;
    }
  }

  /**
   * Insert a Markdown snippet at the editor's real selection.
   *
   * Studio owns what the snippet means (quote/note/reference + provenance);
   * this boundary only converts Markdown, inserts it where the caret is, then
   * emits the resulting document through the normal HTML -> Markdown path.
   * If the editor is not ready, no fallback position is invented.
   */
  async insertMarkdown(markdown: string): Promise<boolean> {
    const snippet = markdown.trim();
    const editor = this.editor;
    if (!snippet || !editor || editor.isDestroyed) return false;

    // onUpdate emits the resulting document.
    editor.chain().focus().insertContent(markdownToHtml(snippet)).run();
    return true;
  }

  /**
   * Replace the open `@` query with a source (issue #1039). A block (a quote
   * or a note) goes after the paragraph the query was typed in, so a sentence
   * is never split; an inline reference takes the query's place.
   */
  replaceSourceQuery(markdown: string, placement: 'block' | 'inline'): boolean {
    const editor = this.editor;
    const range = this.suggestionRange;
    const snippet = markdown.trim();
    if (!editor || editor.isDestroyed || !range || !snippet) return false;

    this.shortcutInserted = 0;
    editor.chain().focus().deleteRange(range).run();
    this.suggestionRange = null;
    if (placement === 'inline') {
      // Unwrap the paragraph Markdown puts around a lone line, so the
      // reference joins the sentence instead of starting a new one.
      const inline = markdownToHtml(snippet).trim().replace(/^<p>([\s\S]*)<\/p>$/, '$1');
      editor.chain().focus().insertContent(inline).run();
      // The reference is italic; the sentence that goes on after it is not.
      editor.view.dispatch(editor.state.tr.setStoredMarks([]));
    } else {
      this.insertBlockAtCaret(markdownToHtml(snippet));
    }
    return true;
  }

  /** Close the open `@` query and leave its text as typed. */
  closeSourceQuery(): void {
    const editor = this.editor;
    if (!editor || editor.isDestroyed || !this.suggestionRange) return;
    exitSuggestion(editor.view, SOURCE_SUGGESTION_KEY);
  }

  /** Remove the open `@` query and leave the caret where it was typed. */
  clearSourceQuery(): boolean {
    const editor = this.editor;
    const range = this.suggestionRange;
    if (!editor || editor.isDestroyed || !range) return false;
    this.shortcutInserted = 0;
    editor.chain().deleteRange(range).run();
    this.suggestionRange = null;
    return true;
  }

  /** Insert Markdown as blocks after the caret's paragraph, never inside it. */
  insertBlockMarkdown(markdown: string): boolean {
    const snippet = markdown.trim();
    if (!snippet || !this.editor || this.editor.isDestroyed) return false;
    this.insertBlockAtCaret(markdownToHtml(snippet));
    return true;
  }

  /** Start an `@` query at the caret, as the keyboard shortcut does. */
  openSourceQuery(): boolean {
    const editor = this.editor;
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

  /**
   * A palette opened with the shortcut and closed with nothing typed leaves
   * no stray `@`: the shortcut typed it, so closing takes it back.
   */
  private dropShortcutAt(): void {
    const inserted = this.shortcutInserted;
    this.shortcutInserted = 0;
    const editor = this.editor;
    const range = this.suggestionRange;
    if (!inserted || !editor || !range || this.suggestionQuery !== '') return;
    const from = range.to - inserted;
    const typed = inserted === 2 ? ' @' : '@';
    queueMicrotask(() => {
      if (editor.isDestroyed || editor.state.doc.textBetween(from, range.to) !== typed) return;
      editor.chain().deleteRange({ from, to: range.to }).run();
    });
  }

  /** The page's current Markdown, straight from the editor. */
  currentMarkdown(): string {
    return this.editor ? htmlToMarkdown(this.editor.getHTML()) : this.initialContent();
  }

  focus(): void {
    this.editor?.commands.focus();
  }

  /**
   * Capture only short-lived editor mechanics: the selection as document
   * positions and the page's scroll offset. Callers never get DOM nodes.
   */
  captureTransientState(): MarkdownEditorTransientState | null {
    const editor = this.editor;
    if (!editor || editor.isDestroyed) return null;

    const { from, to } = editor.state.selection;
    const state: MarkdownEditorTransientState = { bookmark: { from, to } };
    const scrollY = this.scroller.nativeElement.scrollTop;
    if (Number.isFinite(scrollY) && scrollY >= 0) state.scrollY = scrollY;
    return state;
  }

  /**
   * Queue a one-shot restore until the editor is ready and the expected
   * Writing content has actually been applied. This avoids restoring a caret
   * into a previous or empty document during the Studio handoff.
   */
  restoreTransientState(
    state: MarkdownEditorTransientState,
    expectedMarkdown: string,
  ): Promise<boolean> {
    const bookmark = selectionBookmark(state?.bookmark);
    const scrollY =
      typeof state?.scrollY === 'number' && Number.isFinite(state.scrollY) && state.scrollY >= 0
        ? state.scrollY
        : undefined;

    if (!bookmark && scrollY === undefined) return Promise.resolve(false);

    this.pendingTransientRestore?.resolve(false);

    return new Promise<boolean>((resolve) => {
      this.pendingTransientRestore = {
        state: { ...(bookmark ? { bookmark } : {}), ...(scrollY !== undefined ? { scrollY } : {}) },
        expectedMarkdown,
        resolve,
      };
      this.tryApplyTransientRestore();
    });
  }

  private tryApplyTransientRestore(): void {
    const pending = this.pendingTransientRestore;
    const editor = this.editor;
    if (!pending || !editor || this.lastExternalMarkdownApplied !== pending.expectedMarkdown) {
      return;
    }

    this.pendingTransientRestore = null;
    let restored = false;

    const bookmark = selectionBookmark(pending.state.bookmark);
    if (bookmark) {
      // Positions from an older revision may point past the end; clamp rather
      // than throw, since an exact caret is best-effort.
      const max = editor.state.doc.content.size;
      const from = Math.min(Math.max(bookmark.from, 0), max);
      const to = Math.min(Math.max(bookmark.to, from), max);
      // Selection only: focus belongs to whoever just closed a drawer or
      // returned from a source, and the next click or key focuses the page.
      editor.commands.setTextSelection({ from, to });
      restored = true;
    }

    if (pending.state.scrollY !== undefined) {
      const y = pending.state.scrollY;
      const scroller = this.scroller.nativeElement;
      // Focusing can scroll the page. Restore one microtask later so the
      // saved position wins.
      queueMicrotask(() => scroller.scrollTo?.({ top: y }));
      restored = true;
    }

    pending.resolve(restored);
  }

  // ------------------------------------------------------------------
  // Formatting (the selection bubble and keyboard)
  // ------------------------------------------------------------------

  undo(): void {
    this.editor?.chain().focus().undo().run();
  }

  redo(): void {
    this.editor?.chain().focus().redo().run();
  }

  /** A block style from the bubble: applying the active one again undoes it. */
  toggleBlock(kind: BlockKind): void {
    this.setBlock(this.activeBlock() === kind ? 'paragraph' : kind);
  }

  setBlock(kind: string): void {
    const chain = this.editor?.chain().focus();
    if (!chain) return;
    switch (kind as BlockKind) {
      case 'h1':
        chain.setHeading({ level: 1 }).run();
        break;
      case 'h2':
        chain.setHeading({ level: 2 }).run();
        break;
      case 'h3':
        chain.setHeading({ level: 3 }).run();
        break;
      case 'blockquote':
        chain.setParagraph().setBlockquote().run();
        break;
      case 'codeBlock':
        chain.setCodeBlock().run();
        break;
      default:
        chain.setParagraph().unsetBlockquote().run();
    }
  }

  toggleBold(): void {
    this.editor?.chain().focus().toggleBold().run();
  }

  toggleItalic(): void {
    this.editor?.chain().focus().toggleItalic().run();
  }

  toggleBulletList(): void {
    this.editor?.chain().focus().toggleBulletList().run();
  }

  toggleOrderedList(): void {
    this.editor?.chain().focus().toggleOrderedList().run();
  }

  clearFormatting(): void {
    this.editor?.chain().focus().unsetAllMarks().clearNodes().run();
  }

  openUrlPrompt(kind: 'link' | 'image'): void {
    const current = kind === 'link' ? (this.editor?.getAttributes('link')['href'] ?? '') : '';
    this.urlValue.set(current);
    this.urlPrompt.set(kind);
    // The field is created by this change; focus it once it exists, or the
    // next keystrokes would land in (and overwrite) the selected text.
    setTimeout(() => this.urlInput?.nativeElement.focus());
  }

  applyUrl(): void {
    const kind = this.urlPrompt();
    const url = this.urlValue().trim();
    this.urlPrompt.set(null);
    const chain = this.editor?.chain().focus();
    if (!chain || !kind) return;

    if (kind === 'image') {
      if (url) chain.setImage({ src: url }).run();
      return;
    }
    if (!url) {
      chain.extendMarkRange('link').unsetLink().run();
    } else if (this.editor!.state.selection.empty && !this.editor!.isActive('link')) {
      // Nothing selected: the URL itself becomes the linked text.
      chain
        .insertContent({ type: 'text', text: url, marks: [{ type: 'link', attrs: { href: url } }] })
        .run();
    } else {
      chain.extendMarkRange('link').setLink({ href: url }).run();
    }
  }

  cancelUrl(): void {
    this.urlPrompt.set(null);
    this.editor?.commands.focus();
  }

  private refreshToolbar(): void {
    const editor = this.editor;
    if (!editor || editor.isDestroyed) return;
    this.activeBlock.set(
      editor.isActive('heading', { level: 1 })
        ? 'h1'
        : editor.isActive('heading', { level: 2 })
          ? 'h2'
          : editor.isActive('heading', { level: 3 })
            ? 'h3'
            : editor.isActive('codeBlock')
              ? 'codeBlock'
              : editor.isActive('blockquote')
                ? 'blockquote'
                : 'paragraph',
    );
    this.isBold.set(editor.isActive('bold'));
    this.isItalic.set(editor.isActive('italic'));
    this.isBulletList.set(editor.isActive('bulletList'));
    this.isOrderedList.set(editor.isActive('orderedList'));
    this.isLink.set(editor.isActive('link'));
    this.canUndo.set(editor.can().undo());
    this.canRedo.set(editor.can().redo());
  }

  // ------------------------------------------------------------------
  // Internals
  // ------------------------------------------------------------------

  /**
   * Put blocks after the top-level block holding the caret (or in place of
   * it when it is an empty paragraph), then leave the caret on an empty
   * paragraph after them, ready for the next sentence.
   */
  private insertBlockAtCaret(html: string): void {
    const editor = this.editor!;
    const { selection } = editor.state;
    const { $from } = selection;
    let target: number | { from: number; to: number };
    if ($from.depth === 0) {
      // A whole top-level node is selected (an image, a rule): go after it.
      target = selection.to;
    } else {
      const block = $from.node(1);
      const empty = block.type.name === 'paragraph' && block.content.size === 0;
      target = empty ? { from: $from.before(1), to: $from.after(1) } : $from.after(1);
    }
    editor.chain().focus().insertContentAt(target, html, { updateSelection: true }).run();

    const $end = editor.state.selection.$from;
    const after = $end.depth >= 1 ? $end.after(1) : editor.state.selection.to;
    const next = editor.state.doc.resolve(after).nodeAfter;
    const chain = editor.chain().focus();
    if (!(next?.type.name === 'paragraph' && next.content.size === 0)) {
      chain.insertContentAt(after, { type: 'paragraph' });
    }
    chain.setTextSelection(after + 1).scrollIntoView().run();
  }

  private emitMarkdown(): void {
    if (!this.editor) return;
    const markdown = htmlToMarkdown(this.editor.getHTML());
    this.lastEmittedMarkdown = markdown;
    this.contentChange.emit(markdown);
  }

  /**
   * Another document opens: a new editor state, not a transaction. A
   * transaction would sit in the undo history, so Ctrl+Z after switching put
   * the previous writing back on the page and autosaved it into this one.
   */
  private replaceDocument(editor: Editor, markdown: string): void {
    const doc = createDocument(markdownToHtml(markdown), editor.schema);
    editor.view.updateState(EditorState.create({ doc, plugins: editor.state.plugins }));
    this.lastEmittedMarkdown = null;
    this.refreshToolbar();
    this.emitWordCount();
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
    const editor = this.editor;
    if (!editor || editor.isDestroyed) return null;
    try {
      return editor.view.coordsAtPos(editor.state.selection.head).top;
    } catch {
      return null;
    }
  }

  private emitWordCount(): void {
    const editor = this.editor;
    if (!editor) return;
    const text = editor.state.doc.textBetween(0, editor.state.doc.content.size, ' ', ' ');
    this.wordCountChange.emit(countWords(text));
    this.characterCountChange.emit(text.length);
  }

  /**
   * Plain text that is evidently Markdown (copied from a .md file, a chat or a
   * note) is pasted as what it describes. Anything carrying HTML is left to
   * the editor, which maps it onto the page's own nodes.
   */
  private pasteMarkdown(event: ClipboardEvent): boolean {
    const data = event.clipboardData;
    if (!data || !this.editor) return false;
    if (data.types.includes('text/html')) return false;
    // Inside a code block, pasted text is code, never structure.
    if (this.editor.state.selection.$from.parent.type.spec.code) return false;
    const text = data.getData('text/plain');
    if (!looksLikeMarkdown(text)) return false;
    // Line breaks the person pasted stay line breaks.
    this.editor.chain().focus().insertContent(markdownToHtml(text, { breaks: true })).run();
    return true;
  }

  /**
   * Typewriter scroll: keep the caret line near 45% of the page.
   */
  private followCaret(): void {
    const editor = this.editor;
    if (!this.typewriter() || !editor || editor.isDestroyed) return;
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

/** Words as a reader counts them: runs of letters or digits, any script. */
export function countWords(text: string): number {
  return text.match(/[\p{L}\p{N}][\p{L}\p{N}'’\-]*/gu)?.length ?? 0;
}

/** Whether pasted plain text is written in Markdown rather than prose. */
export function looksLikeMarkdown(text: string): boolean {
  if (!text || !text.includes('\n') && !/[*_`#>\[]/.test(text)) return false;
  return (
    /^(#{1,6}\s|>\s?|[-*+]\s|\d+[.)]\s|```|\|.*\|\s*$)/m.test(text) ||
    /\*\*[^*\n]+\*\*|\[[^\]\n]+\]\([^)\s]+\)/.test(text)
  );
}

function selectionBookmark(value: unknown): { from: number; to: number } | null {
  if (!value || typeof value !== 'object') return null;
  const { from, to } = value as { from?: unknown; to?: unknown };
  if (typeof from !== 'number' || typeof to !== 'number') return null;
  if (!Number.isInteger(from) || !Number.isInteger(to)) return null;
  return { from, to };
}

function plainRect(rect: DOMRect): SourceSuggestionState['rect'] {
  return { top: rect.top, bottom: rect.bottom, left: rect.left, right: rect.right };
}

function coarsePointer(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(pointer: coarse)').matches;
}
