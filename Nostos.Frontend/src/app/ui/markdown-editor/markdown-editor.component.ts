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
import { Editor, createDocument } from '@tiptap/core';
import { EditorState } from '@tiptap/pm/state';
import StarterKit from '@tiptap/starter-kit';
import Placeholder from '@tiptap/extension-placeholder';
import Typography from '@tiptap/extension-typography';
import Image from '@tiptap/extension-image';
import { TableKit } from '@tiptap/extension-table';

import { DropdownComponent, DropdownOption } from '../dropdown/dropdown.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { htmlToMarkdown, markdownToHtml } from './markdown-conversion';

export interface MarkdownEditorTransientState {
  /** The selection, as ProseMirror document positions. */
  bookmark?: unknown;
  scrollY?: number;
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
  imports: [DropdownComponent, IconButtonComponent],
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
  /**
   * Typewriter mode: keep the caret line near 45% of the page by scrolling on
   * caret activity. Owned by the studio (toggle + persistence); the editor
   * only follows while this is true.
   */
  typewriter = input<boolean>(false);

  @ViewChild('mount', { static: true }) private mount!: ElementRef<HTMLElement>;
  @ViewChild('scroller', { static: true }) private scroller!: ElementRef<HTMLElement>;
  @ViewChild('urlInput') private urlInput?: ElementRef<HTMLInputElement>;

  /** True until the editor exists, so the empty page never flashes a caret. */
  readonly editorStarting = signal(true);

  readonly blockOptions: readonly DropdownOption[] = [
    { value: 'paragraph', label: 'Paragraph' },
    { value: 'h1', label: 'Heading 1' },
    { value: 'h2', label: 'Heading 2' },
    { value: 'h3', label: 'Heading 3' },
    { value: 'blockquote', label: 'Quote' },
    { value: 'codeBlock', label: 'Code' },
  ];

  /** Toolbar state, refreshed on every transaction. */
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
  private pendingTransientRestore: PendingTransientRestore | null = null;

  constructor() {
    effect(() => {
      const on = this.typewriter();
      this.toggleTypewriterLayout(on);
      if (on) queueMicrotask(() => this.followCaret());
    });

    // External content (opening another document) replaces the page only when
    // it really differs, so a save echo never moves the caret.
    effect(() => {
      const markdown = this.initialContent();
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
    this.editorStarting.set(false);
    this.refreshToolbar();
    this.emitWordCount();
    this.tryApplyTransientRestore();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.pendingTransientRestore?.resolve(false);
    this.pendingTransientRestore = null;
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
  // Toolbar
  // ------------------------------------------------------------------

  undo(): void {
    this.editor?.chain().focus().undo().run();
  }

  redo(): void {
    this.editor?.chain().focus().redo().run();
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
