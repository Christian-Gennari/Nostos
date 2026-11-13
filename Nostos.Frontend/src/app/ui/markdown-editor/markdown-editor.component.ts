import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  ViewEncapsulation,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import { Editor, Range, createDocument } from '@tiptap/core';
import { EditorState, type Transaction } from '@tiptap/pm/state';
import StarterKit from '@tiptap/starter-kit';
import Placeholder from '@tiptap/extension-placeholder';
import Typography from '@tiptap/extension-typography';
import Image from '@tiptap/extension-image';
import { TableKit } from '@tiptap/extension-table';
import BubbleMenu from '@tiptap/extension-bubble-menu';
import { exitSuggestion } from '@tiptap/suggestion';

import { htmlToMarkdown, markdownToHtml } from './markdown-conversion';
import { SourceSuggestion } from './source-suggestion.extension';
import { FootnoteRef, tidyFootnotes } from './footnote.extension';
import { FootnoteAuthoring, insertFootnoteAtSelection, insertFootnoteIn } from './footnote-authoring';
import { Citation } from './citation.extension';
import { CitationRenderer, bodyEndPosition, citationBooks, citationSignature, footnoteInsertPosition, refreshCitations } from './live-citations';
import { SoundPack } from './typewriter-sounds';
import { BLOCK_MENU_KEY, BlockMenu, BlockMenuAction, filterBlockMenu } from './block-menu';
import { BlockKind, EditorSelectionBubbleComponent } from './editor-selection-bubble.component';
import { BlockMenuOverlayState, EditorBlockMenuComponent, computeBlockMenuPlacement } from './editor-block-menu.component';
import { EditorSourceSuggestionComponent, SourceSuggestionState } from './editor-source-suggestion.component';
import { TypewriterScrollDirective } from './typewriter-scroll.directive';
import { TypewriterSoundDirective } from './typewriter-sound.directive';

export type { SourceSuggestionState } from './editor-source-suggestion.component';
export { caretScrollDelta } from './typewriter-scroll.directive';

/** A footnote that goes in with an insertion: its number and the note's Markdown. */
export interface InsertedFootnote {
  number: number;
  markdown: string;
}

/**
 * Marks `tr` as part of the undo step before it (prosemirror-history treats an
 * appended transaction so), so a quote and the footnote it brings, or a
 * renumbering, come out with one undo.
 */
function joinPreviousUndo<T extends { setMeta(key: string, value: unknown): T }>(tr: T): T {
  return tr.setMeta('appendedTransaction', tr);
}

export interface MarkdownEditorTransientState {
  /** The selection, as ProseMirror document positions. */
  bookmark?: unknown;
  scrollY?: number;
  /**
   * Instead of a bookmark: a fresh line after the writing's own text, before
   * its footnotes (issue #1220), for a writing that is ready to be written on.
   */
  at?: 'body-end';
}

interface PendingTransientRestore {
  state: MarkdownEditorTransientState;
  expectedMarkdown: string;
  resolve: (restored: boolean) => void;
}

/** The events through which a writer's keyboard changes the page. */
const KEY_INPUT_EVENTS = ['keydown', 'beforeinput', 'input', 'compositionupdate'] as const;

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
  imports: [
    EditorSelectionBubbleComponent,
    EditorBlockMenuComponent,
    EditorSourceSuggestionComponent,
    TypewriterScrollDirective,
    TypewriterSoundDirective,
  ],
  templateUrl: './markdown-editor.component.html',
  styleUrl: './markdown-editor.component.css',
  // ProseMirror renders the page's nodes itself, so emulated encapsulation
  // could never reach them. Every rule is scoped under `app-markdown-editor`.
  changeDetection: ChangeDetectionStrategy.OnPush,
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
  /** Browser spell checking on the page (a page setting, #1041). */
  spellcheck = input<boolean>(true);
  /** Typewriter sounds on keystrokes (a page setting, #1042). */
  sounds = input<boolean>(false);
  /** 0–1. */
  soundVolume = input<number>(0.6);
  /** Which recordings play (#1065). */
  soundPack = input<SoundPack>('typewriter');

  /** The `@` query while one is open, and null when it closes. */
  sourceSuggestion = output<SourceSuggestionState | null>();
  /** "Bibliography" from the `/` menu (issue #1109): the Studio builds it and inserts it here. */
  bibliographyRequested = output<void>();
  /**
   * The writing's live citations changed: one went in, moved or went out, or
   * another writing opened (issue #1157). Carries a signature of them, so the
   * Studio can tell a real change from an edit elsewhere.
   */
  citationsChange = output<string>();
  /** Keys pressed while an `@` query is open go here first; true consumes one. */
  sourceSuggestionKeyDown = input<((event: KeyboardEvent) => boolean) | null>(null);
  /** The palette's highlighted option, announced through the page's ARIA. */
  sourceSuggestionActiveId = input<string | null>(null);

  @ViewChild('mount', { static: true }) private mount!: ElementRef<HTMLElement>;
  @ViewChild('scroller', { static: true }) private scroller!: ElementRef<HTMLElement>;
  @ViewChild('scroller', { static: true, read: TypewriterScrollDirective }) private typewriterScroll!: TypewriterScrollDirective;
  @ViewChild('scroller', { static: true, read: TypewriterSoundDirective }) private typewriterSound!: TypewriterSoundDirective;
  @ViewChild('bubble', { static: true, read: ElementRef }) private bubble!: ElementRef<HTMLElement>;
  @ViewChild('bubble', { static: true }) private bubbleComponent!: EditorSelectionBubbleComponent;
  @ViewChild('blockMenuHost', { static: true }) private blockMenuHost!: EditorBlockMenuComponent;
  @ViewChild('sourceSuggestionHost', { static: true }) private sourceSuggestionHost!: EditorSourceSuggestionComponent;

  /** The editor's contenteditable element and instance, once they exist. */
  readonly editorDom = signal<HTMLElement | null>(null);
  readonly editorInstance = signal<Editor | null>(null);
  readonly sourceSuggestionOpen = signal(false);

  /** True until the editor exists, so the empty page never flashes a caret. */
  readonly editorStarting = signal(true);

  /** The `/` menu while open (issue #1043). */
  readonly blockMenu = signal<BlockMenuOverlayState | null>(null);
  readonly blockMenuActive = signal(0);
  /** The menu's second step for Image: the address field. */
  readonly blockMenuImage = signal(false);
  readonly blockMenuImageUrl = signal('');
  private blockMenuRange: Range | null = null;
  private blockMenuQuery = '';
  /**
   * The "/query" an image will replace, kept while focus is in the address
   * field and mapped through every change so it never points at other text.
   */
  private blockImageRange: Range | null = null;
  readonly blockMenuPlacement = computed(() => computeBlockMenuPlacement(this.blockMenu()?.rect));

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
  /** True while an insertion puts a footnote marker and its note in. */
  private footnotesPaused = false;
  /** The last Markdown this editor emitted, so a save echo costs nothing. */
  private lastEmittedMarkdown: string | null = null;
  private destroyed = false;
  private lastExternalMarkdownApplied: string | null = null;
  private pendingTransientRestore: PendingTransientRestore | null = null;

  constructor() {
    // Spell check is one of the view's attributes, so ProseMirror itself keeps
    // it on every redraw; a transaction asks it to read the setting again.
    effect(() => {
      this.spellcheck();
      const editor = this.editor;
      if (editor && !editor.isDestroyed && this.editorDom()) {
        editor.view.dispatch(editor.state.tr.setMeta('nostosSpellcheck', true).setMeta('addToHistory', false));
      }
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
    // The bubble plugin attaches its element when a selection appears and
    // removes it when it goes; rendered in place it would sit on the page as
    // a bar. Angular keeps updating its bindings while it is detached.
    this.bubble.nativeElement.remove();

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
        // `--` stays two hyphens: the rule cannot tell prose from a table
        // separator row or code, and an em dash there breaks the Markdown.
        Typography.configure({ emDash: false }),
        // Writings saved under TinyMCE can hold pasted images as data URIs.
        Image.configure({ allowBase64: true }),
        TableKit.configure({ table: { resizable: false } }),
        FootnoteRef.configure({ paused: () => this.footnotesPaused }),
        FootnoteAuthoring,
        Citation,
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
            onHide: () => {
              this.urlPrompt.set(null);
              this.bubbleComponent.clearFocusTimer();
            },
          },
          // Never over an open @ query, an image or a code block.
          shouldShow: ({ editor, from, to, element }) => {
            if (editor.isDestroyed) return false;
            if (element.contains(element.ownerDocument.activeElement)) return true;
            if (from === to || !editor.isFocused || this.sourceSuggestionHost.isOpen()) return false;
            return !editor.isActive('codeBlock') && !editor.isActive('image');
          },
        }),
        BlockMenu.configure({
          onChange: (state) => {
            this.blockMenuRange = state?.range ?? null;
            if (!state) {
              // The address step keeps the menu up while its field has focus.
              if (!this.blockMenuImage()) {
                this.blockMenuHost.clearFocusTimer();
                this.blockMenu.set(null);
              }
              return;
            }
            const items = filterBlockMenu(state.query);
            // A new query starts again from its best match.
            if (!this.blockMenu() || state.query !== this.blockMenuQuery) this.blockMenuActive.set(0);
            this.blockMenuQuery = state.query;
            this.blockMenu.set({ items, rect: state.rect ? plainRect(state.rect) : null });
          },
          onKeyDown: (event) => this.blockMenuKey(event),
        }),
        SourceSuggestion.configure(this.sourceSuggestionHost.extensionOptions()),
      ],
      content: markdownToHtml(markdown),
      editorProps: {
        attributes: () => ({
          class: 'nostos-manuscript-body',
          spellcheck: String(this.spellcheck()),
          'aria-label': 'Document',
          'aria-multiline': 'true',
          role: 'textbox',
        }),
        handlePaste: (_view, event) => this.pasteMarkdown(event),
      },
      onUpdate: ({ transaction }) => {
        // Typing is the hot path: the page is not serialised, counted and
        // scanned for citations on every key (issue #1392), only once the
        // writer pauses. Everything else (an insertion, a paste) reports now.
        if (this.isTypingTransaction(transaction)) this.scheduleChangeReport();
        else this.flushPendingChange(true);
      },
      onTransaction: ({ transaction }) => {
        if (this.blockImageRange && transaction.docChanged) {
          const { from, to } = this.blockImageRange;
          const mapped = { from: transaction.mapping.map(from, 1), to: transaction.mapping.map(to, -1) };
          this.blockImageRange = mapped.from < mapped.to ? mapped : null;
        }
        this.refreshToolbar();
      },
      // Back on the page, the image address step is abandoned.
      onFocus: () => {
        if (this.blockMenuImage()) this.cancelBlockImage(false);
      },
    });

    this.lastExternalMarkdownApplied ??= markdown;
    const dom = this.editor.view.dom as HTMLElement;
    this.sourceSuggestionHost.attachEditor(this.editor);
    this.typewriterScroll.attachEditor(this.editor);
    this.typewriterSound.attachTarget(dom);
    this.editorInstance.set(this.editor);
    this.editorDom.set(dom);
    for (const type of KEY_INPUT_EVENTS) this.scroller.nativeElement.addEventListener(type, this.onKeyInputCapture, true);
    this.scroller.nativeElement.addEventListener('scroll', this.followBlockMenu, { passive: true });
    this.editorStarting.set(false);
    this.refreshToolbar();
    this.emitWordCount();
    this.emitCitations(true);
    this.tryApplyTransientRestore();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.pendingTransientRestore?.resolve(false);
    this.pendingTransientRestore = null;
    this.bubbleComponent?.clearFocusTimer();
    this.blockMenuHost?.clearFocusTimer();
    this.bubble?.nativeElement.remove();
    this.typewriterScroll?.attachEditor(null);
    this.typewriterSound?.attachTarget(null);
    this.sourceSuggestionHost?.attachEditor(null);
    if (this.editor) {
      this.scroller.nativeElement.removeEventListener('scroll', this.followBlockMenu);
      for (const type of KEY_INPUT_EVENTS) this.scroller.nativeElement.removeEventListener(type, this.onKeyInputCapture, true);
      // The last keystrokes may still be inside the debounce window upstream.
      this.clearChangeTimers();
      this.emitMarkdown();
      this.editor.destroy();
      this.editor = null;
      this.editorInstance.set(null);
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
  async insertMarkdown(markdown: string, footnote: InsertedFootnote | null = null): Promise<boolean> {
    const snippet = markdown.trim();
    const editor = this.editor;
    if (!snippet || !editor || editor.isDestroyed) return false;

    // onUpdate emits the resulting document.
    return this.withFootnote(footnote, () => {
      editor.chain().focus().insertContent(markdownToHtml(snippet)).run();
      return true;
    });
  }

  /**
   * Replace the open `@` query with a source (issue #1039). A block (a quote
   * or a note) goes after the paragraph the query was typed in, so a sentence
   * is never split; an inline reference takes the query's place.
   */
  replaceSourceQuery(markdown: string, placement: 'block' | 'inline', footnote: InsertedFootnote | null = null): boolean {
    const snippet = markdown.trim();
    if (!this.sourceSuggestionHost.canReplace(snippet)) return false;
    return this.withFootnote(footnote, () =>
      this.sourceSuggestionHost.replaceQuery(snippet, placement, (html) => this.insertBlockAtCaret(html)),
    );
  }

  /** Close the open `@` query and leave its text as typed. */
  closeSourceQuery(): void {
    this.sourceSuggestionHost.closeQuery();
  }

  /** Remove the open `@` query and leave the caret where it was typed. */
  clearSourceQuery(): boolean {
    return this.sourceSuggestionHost.clearQuery();
  }

  /**
   * Close the open `@` query but remember what it was, for a choice that is
   * finished somewhere else (a passage read in the viewer). The typed words
   * stay on the page until the choice lands, so closing the viewer without
   * quoting loses nothing.
   */
  holdSourceQuery(): boolean {
    return this.sourceSuggestionHost.holdQuery();
  }

  /** Remove the held `@` query, if the page still holds exactly those words there. */
  releaseHeldQuery(): void {
    this.sourceSuggestionHost.releaseHeldQuery();
  }

  /** Insert Markdown as blocks after the caret's paragraph, never inside it. */
  insertBlockMarkdown(markdown: string, footnote: InsertedFootnote | null = null): boolean {
    const snippet = markdown.trim();
    if (!snippet || !this.editor || this.editor.isDestroyed) return false;
    return this.withFootnote(footnote, () => {
      this.insertBlockAtCaret(markdownToHtml(snippet));
      return true;
    });
  }

  /**
   * An insertion that brings its own footnote (issue #1109): the marker and
   * its note go in together, are tidied together (renumbered in reading
   * order), and come out with one undo.
   */
  private withFootnote(footnote: InsertedFootnote | null, insert: () => boolean): boolean {
    if (!footnote) return insert();
    this.footnotesPaused = true;
    let inserted = false;
    try {
      inserted = insert();
      if (inserted) this.appendFootnote(footnote.number, footnote.markdown);
    } finally {
      this.footnotesPaused = false;
    }
    const editor = this.editor;
    const tidy = editor && !editor.isDestroyed ? tidyFootnotes(editor.state) : null;
    if (tidy) editor!.view.dispatch(joinPreviousUndo(tidy));
    return inserted;
  }

  /**
   * A footnote of the writer's own (issue #1342): a marker after the
   * selection and an empty note at the end, with the caret in the note.
   */
  addFootnote(): boolean {
    const editor = this.editor;
    return !!editor && insertFootnoteAtSelection(editor);
  }

  /** The number for a new footnote: one past the highest marker in the writing. */
  nextFootnoteNumber(): number {
    let highest = 0;
    this.editor?.state.doc.descendants((node) => {
      if (node.type.name === 'footnoteRef') highest = Math.max(highest, Number(node.attrs['number']) || 0);
    });
    return highest + 1;
  }

  /**
   * Add a footnote's text at the end of the writing (issue #1109), where
   * Markdown footnotes live, without moving the caret from where the marker
   * went in.
   */
  appendFootnote(number: number, markdown: string): boolean {
    const editor = this.editor;
    const text = markdown.trim();
    if (!editor || editor.isDestroyed || !text) return false;
    // After the other notes, ahead of a bibliography at the end (issue #1157).
    const at = footnoteInsertPosition(editor.state.doc);
    editor
      .chain()
      .command(({ tr }) => {
        joinPreviousUndo(tr);
        return true;
      })
      .insertContentAt(at, markdownToHtml(`[^${number}]: ${text}`), { updateSelection: false })
      .run();
    return true;
  }

  /** Whether the writing holds a bibliography the Studio wrote (issue #1157). */
  hasBibliography(): boolean {
    let found = false;
    this.editor?.state.doc.descendants((node) => {
      if (found) return false;
      if (node.type.name === 'citation' && node.attrs['as'] === 'bib') found = true;
      return !found;
    });
    return found;
  }

  /** The books the writing's live citations point at (issue #1157). */
  citedBooks(): string[] {
    return this.editor && !this.editor.isDestroyed ? citationBooks(this.editor.state.doc) : [];
  }

  /**
   * Write every live citation again for `renderer` (issue #1157). Kept out of
   * the undo history: it follows the book's details and the chosen style,
   * which an undo of the writer's own typing should not reverse.
   */
  refreshCitations(renderer: CitationRenderer): boolean {
    const editor = this.editor;
    if (!editor || editor.isDestroyed) return false;
    const tr = refreshCitations(editor.state, renderer);
    if (!tr) return false;
    editor.view.dispatch(tr.setMeta('addToHistory', false));
    return true;
  }

  /** Start an `@` query at the caret, as the keyboard shortcut does. */
  openSourceQuery(): boolean {
    return this.sourceSuggestionHost.openQuery();
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

    const at = state?.at === 'body-end' ? state.at : undefined;

    if (!bookmark && scrollY === undefined && !at) return Promise.resolve(false);

    this.pendingTransientRestore?.resolve(false);

    return new Promise<boolean>((resolve) => {
      this.pendingTransientRestore = {
        state: {
          ...(bookmark ? { bookmark } : {}),
          ...(scrollY !== undefined ? { scrollY } : {}),
          ...(at ? { at } : {}),
        },
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

    if (pending.state.at === 'body-end') {
      this.focusBodyEnd(editor);
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

  /**
   * The caret on an empty line after the writing's own text (issue #1220),
   * ahead of its footnotes. An empty paragraph already there is reused, so
   * the Markdown the writing saves does not change.
   */
  private focusBodyEnd(editor: Editor): void {
    const doc = editor.state.doc;
    const end = bodyEndPosition(doc);
    const before = doc.resolve(end).nodeBefore;
    const chain = editor.chain().focus();
    if (before?.type.name === 'paragraph' && before.content.size === 0) {
      chain.setTextSelection(end - 1);
    } else {
      chain.insertContentAt(end, { type: 'paragraph' }).setTextSelection(end + 1);
    }
    chain.scrollIntoView().run();
  }

  // ------------------------------------------------------------------
  // The / menu (issue #1043)
  // ------------------------------------------------------------------

  private blockMenuKey(event: KeyboardEvent): boolean {
    const menu = this.blockMenu();
    // While the address field is up, its keys are its own, not the list's.
    if (!menu || menu.items.length === 0 || this.blockMenuImage()) return false;
    const count = menu.items.length;
    if (event.key === 'ArrowDown') {
      this.blockMenuActive.update((i) => (i + 1) % count);
      return true;
    }
    if (event.key === 'ArrowUp') {
      this.blockMenuActive.update((i) => (i - 1 + count) % count);
      return true;
    }
    if (event.key === 'Enter' || event.key === 'Tab') {
      this.runBlockAction(menu.items[this.blockMenuActive()].action);
      return true;
    }
    return false;
  }

  /** Replaces the "/query" with the chosen block. */
  runBlockAction(action: BlockMenuAction): void {
    const editor = this.editor;
    const range = this.blockMenuRange;
    if (!editor || !range) return;
    if (action === 'image') {
      // A second step in the same menu: the address of the image.
      this.blockImageRange = range;
      this.blockMenuImage.set(true);
      this.blockMenuImageUrl.set('');
      return;
    }
    if (action === 'footnote') {
      // The "/query" makes way for the marker; the caret goes on into the note.
      const tr = editor.state.tr;
      if (insertFootnoteIn(tr, range.from, range.to)) editor.view.dispatch(tr.scrollIntoView());
      editor.commands.focus();
      this.closeBlockMenu();
      return;
    }
    const chain = editor.chain().focus().deleteRange(range);
    switch (action) {
      case 'h1':
        chain.setHeading({ level: 1 });
        break;
      case 'h2':
        chain.setHeading({ level: 2 });
        break;
      case 'h3':
        chain.setHeading({ level: 3 });
        break;
      case 'blockquote':
        chain.setBlockquote();
        break;
      case 'bulletList':
        chain.toggleBulletList();
        break;
      case 'orderedList':
        chain.toggleOrderedList();
        break;
      case 'divider':
        chain.setHorizontalRule();
        break;
      case 'codeBlock':
        chain.setCodeBlock();
        break;
      case 'table':
        chain.insertTable({ rows: 3, cols: 2, withHeaderRow: true });
        break;
      case 'source':
        // Hands over to the @ palette on the same line.
        chain.insertContent('@');
        break;
      case 'bibliography':
        // The emptied line is where the Studio puts the list.
        break;
    }
    chain.run();
    this.closeBlockMenu();
    if (action === 'bibliography') this.bibliographyRequested.emit();
  }

  applyBlockImage(): void {
    const editor = this.editor;
    const range = this.blockImageRange;
    this.blockImageRange = null;
    const url = this.blockMenuImageUrl().trim();
    this.blockMenuImage.set(false);
    this.blockMenuHost?.clearFocusTimer();
    if (editor && url) {
      // Replace the "/query" if it is still there; otherwise insert at the caret.
      const intact = range && editor.state.doc.textBetween(range.from, range.to).startsWith('/');
      const chain = editor.chain().focus();
      // The caret may have moved since (an insert from the Studio): the image
      // goes on the emptied line, not wherever the caret is now.
      if (intact) chain.deleteRange(range).setTextSelection(range.from);
      chain.setImage({ src: url }).run();
    } else {
      editor?.commands.focus();
    }
    this.closeBlockMenu();
  }

  cancelBlockImage(refocus = true): void {
    this.blockImageRange = null;
    this.blockMenuImage.set(false);
    this.closeBlockMenu();
    if (refocus) this.editor?.commands.focus();
  }

  /** Keeps the menu on its line while the page scrolls under it (typewriter mode included). */
  private readonly followBlockMenu = () => {
    const menu = this.blockMenu();
    const range = this.blockImageRange ?? this.blockMenuRange;
    const view = this.editor?.view;
    if (!menu || !range || !view) return;
    try {
      const at = view.coordsAtPos(range.from);
      this.blockMenu.set({ ...menu, rect: { top: at.top, bottom: at.bottom, left: at.left, right: at.right } });
    } catch {
      // The range has left the document; the next update closes the menu.
    }
  };

  private closeBlockMenu(): void {
    this.blockMenuHost?.clearFocusTimer();
    this.blockMenu.set(null);
    this.blockMenuActive.set(0);
    if (this.editor && !this.editor.isDestroyed && this.blockMenuRange) {
      exitSuggestion(this.editor.view, BLOCK_MENU_KEY);
    }
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
  }

  applyUrl(): void {
    const kind = this.urlPrompt();
    const url = this.urlValue().trim();
    this.urlPrompt.set(null);
    this.bubbleComponent?.clearFocusTimer();
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
    this.bubbleComponent?.clearFocusTimer();
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

  /**
   * How long the writer must pause before the page is reported upstream, and
   * the longest a stretch of typing goes unreported (issue #1392). Serialising
   * the page to Markdown costs time in proportion to its length.
   */
  static readonly CHANGE_REPORT_IDLE_MS = 400;
  static readonly CHANGE_REPORT_MAX_MS = 8000;

  private changeIdleTimer: ReturnType<typeof setTimeout> | null = null;
  private changeMaxTimer: ReturnType<typeof setTimeout> | null = null;
  /** When the writer last pressed a key or the keyboard last changed the text. */
  private lastKeyInputAt = Number.NEGATIVE_INFINITY;

  private readonly onKeyInputCapture = () => {
    this.lastKeyInputAt = performance.now();
  };

  /**
   * Whether a transaction is the writer typing. ProseMirror tags no ordinary
   * keystroke, so it is told by what just happened: a key or the keyboard
   * touched the page a moment ago. An insertion the Studio makes is not.
   */
  private isTypingTransaction(transaction: Transaction): boolean {
    const uiEvent = transaction.getMeta('uiEvent');
    if (uiEvent === 'paste' || uiEvent === 'drop' || uiEvent === 'cut') return false;
    return performance.now() - this.lastKeyInputAt < 150;
  }

  private scheduleChangeReport(): void {
    if (this.changeIdleTimer !== null) clearTimeout(this.changeIdleTimer);
    this.changeIdleTimer = setTimeout(() => this.flushPendingChange(), MarkdownEditorComponent.CHANGE_REPORT_IDLE_MS);
    this.changeMaxTimer ??= setTimeout(() => this.flushPendingChange(), MarkdownEditorComponent.CHANGE_REPORT_MAX_MS);
  }

  private clearChangeTimers(): void {
    if (this.changeIdleTimer !== null) clearTimeout(this.changeIdleTimer);
    if (this.changeMaxTimer !== null) clearTimeout(this.changeMaxTimer);
    this.changeIdleTimer = null;
    this.changeMaxTimer = null;
  }

  /**
   * Report the page now if typing has not been reported yet: its Markdown, word
   * count and citations. The Studio calls this before it reads the text to
   * save it. `always` reports even when nothing is waiting (a non-typing edit).
   */
  flushPendingChange(always = false): void {
    const waiting = this.changeIdleTimer !== null || this.changeMaxTimer !== null;
    this.clearChangeTimers();
    if (!waiting && !always) return;
    if (!this.editor || this.editor.isDestroyed) return;
    this.emitMarkdown();
    this.emitWordCount();
    this.emitCitations();
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
    this.clearChangeTimers();
    const doc = createDocument(markdownToHtml(markdown), editor.schema);
    editor.view.updateState(EditorState.create({ doc, plugins: editor.state.plugins }));
    this.lastEmittedMarkdown = null;
    this.refreshToolbar();
    this.emitWordCount();
    this.emitCitations(true);
  }

  /** The last citation signature emitted; `force` emits even an unchanged one (another writing opened). */
  private lastCitations: string | null = null;

  private emitCitations(force = false): void {
    if (!this.editor || this.editor.isDestroyed) return;
    const signature = citationSignature(this.editor.state.doc);
    if (!force && signature === this.lastCitations) return;
    this.lastCitations = signature;
    this.citationsChange.emit(signature);
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
