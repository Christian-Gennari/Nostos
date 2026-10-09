import { Component, input, output, effect, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';

import TurndownService from 'turndown';
import { marked } from 'marked';
import { ThemeService } from '../../core/services/theme.service';
import { TinyMceApi, TinyMceLoader } from './tinymce-loader.service';

/** Shared desktop manuscript and toolbar measure. The old 600px cap made the
 * Studio feel like a phone column on wide monitors, even with both rails closed.
 * 820px keeps a readable column while using substantially more of the desk.
 * The existing narrow-viewport body styles still own phone geometry. */
const STUDIO_MANUSCRIPT_MEASURE_PX = 820;
const STUDIO_MANUSCRIPT_GUTTER = 'clamp(20px, 4%, 40px)';
const STUDIO_TOOLBAR_FIRST_GLYPH_INSET_PX = 12;

/**
 * Editor content page — warm ink on a white paper sheet, theme-independent.
 * The paper intentionally does NOT follow the app theme: dark/sepia modes
 * describe the room around the manuscript, never the manuscript itself.
 * (Expert design §4 — keep verbatim.)
 */
const NOSTOS_EDITOR_CONTENT_CSS = `
  @import url('https://fonts.googleapis.com/css2?family=Hanken+Grotesk:wght@500;600&family=Newsreader:ital,wght@0,400;0,500;0,600;1,400;1,500&display=swap');

  :root {
    color-scheme: light dark;

    --paper: #ffffff;
    --ink: #121316;
    --ink-strong: #211f1c;
    --ink-soft: #4A4D54;
    --ink-faint: #94979E;

    --rule: #E5E7EB;
    --rule-strong: #C7C6CB;

    --link: #526d87;
    --link-hover: #394f65;
    --link-rule: rgba(82, 109, 135, 0.42);
    --selection: rgba(104, 126, 148, 0.24);

    --quote-bg: #F7F7F8;
    --quote-rule: #94979E;

    --code-bg: #F3F3F4;
    --code-ink: #4A4D54;

    --table-head-bg: #faf9f7;
  }

  :root[data-theme='dark'] {
    color-scheme: dark;

    --paper: #121318;
    --ink: #f0f1f4;
    --ink-strong: #f0f1f4;
    --ink-soft: #c4c7d0;
    --ink-faint: #8b909f;

    --rule: #2a2d37;
    --rule-strong: #3a3f4d;

    --link: #a9cfc2;
    --link-hover: #c4ebde;
    --link-rule: rgba(169, 207, 194, 0.42);
    --selection: rgba(169, 207, 194, 0.25);

    --quote-bg: #181a20;
    --quote-rule: #44495a;

    --code-bg: #0d0e11;
    --code-ink: #f0f1f4;

    --table-head-bg: #181a20;
  }

  html {
    min-height: 100%;
    background: var(--paper);
    scroll-behavior: auto;
    scrollbar-width: thin;
    scrollbar-color: var(--rule-strong) transparent;
  }

  html::-webkit-scrollbar {
    width: 6px;
  }

  html::-webkit-scrollbar-track {
    background: transparent;
  }

  html::-webkit-scrollbar-thumb {
    background-color: var(--rule-strong);
    border-radius: 999px;
  }

  html::-webkit-scrollbar-thumb:hover {
    background-color: var(--ink-faint);
  }

  body {
    box-sizing: border-box;
    width: 100%;
    max-width: ${STUDIO_MANUSCRIPT_MEASURE_PX}px;
    margin: 0 auto;
    min-height: 100%;
    padding:
      clamp(2rem, 4vw, 3.5rem)
      ${STUDIO_MANUSCRIPT_GUTTER}
      7rem;

    color: var(--ink);
    background: var(--paper);

    font-family: Newsreader, Georgia, 'Times New Roman', serif;
    font-size: 18px;
    font-weight: 400;
    line-height: 1.8;

    text-rendering: optimizeLegibility;
    font-kerning: normal;
    font-variant-ligatures: common-ligatures;
    overflow-wrap: break-word;
    outline: none !important;
  }

  body.nostos-typewriter {
    /* Typewriter mode needs real scroll runway above and below the manuscript.
       Without this, the first and last lines can never reach the 45% target. */
    padding-top: 45vh;
    padding-bottom: 55vh;
  }

  body:focus,
  body:focus-visible {
    outline: none !important;
  }

  ::selection {
    color: var(--ink);
    background: var(--selection);
  }

  p {
    margin: 0 0 1.35em;
  }

  h1,
  h2,
  h3 {
    color: var(--ink);
    font-family: 'Hanken Grotesk', system-ui, sans-serif;
    font-weight: 600;
    font-style: normal;
    letter-spacing: -0.025em;
    text-wrap: balance;
  }

  h1 {
    margin: 0 0 1.25em;
    font-size: 2rem;
    line-height: 1.18;
  }

  h2 {
    margin: 2.4em 0 0.75em;
    font-size: 1.42rem;
    line-height: 1.25;
  }

  h3 {
    margin: 2em 0 0.65em;
    font-size: 1.08rem;
    line-height: 1.35;
    letter-spacing: -0.012em;
  }

  h1 + h2,
  h2 + h3 {
    margin-top: 1.2em;
  }

  h1 + p,
  h2 + p,
  h3 + p {
    margin-top: 0;
  }

  /* Emphasis ink is a ROLE, not a literal. It was the literal light ink, so on
     the dark sheet bold text painted near-black on near-black — measured 1.13:1
     against the paper when the studio editor was in dark mode. The light sheet
     keeps its warm emphasis ink; on dark it follows the app's primary ink. */
  strong {
    color: var(--ink-strong);
    font-weight: 600;
  }

  em {
    font-style: italic;
  }

  a {
    color: var(--link);
    text-decoration-line: underline;
    text-decoration-color: var(--link-rule);
    text-decoration-thickness: 1px;
    text-underline-offset: 0.18em;
  }

  a:hover {
    color: var(--link-hover);
    text-decoration-color: currentColor;
  }

  blockquote {
    margin: 2.1rem 0;
    padding: 0.15rem 0 0.15rem 1.25rem;

    color: var(--ink-soft);
    background: transparent;
    border-left: 2px solid var(--quote-rule);

    font-size: 1.03em;
    font-style: italic;
    line-height: 1.75;
  }

  blockquote p:last-child {
    margin-bottom: 0;
  }

  ul,
  ol {
    margin: 0 0 1.4em;
    padding-left: 1.6em;
  }

  li {
    margin: 0.3em 0;
    padding-left: 0.18em;
  }

  li::marker {
    color: var(--ink-faint);
  }

  code {
    padding: 0.12em 0.34em;
    color: var(--code-ink);
    background: var(--code-bg);
    border: 1px solid var(--rule);
    border-radius: 4px;

    font-family:
      'IBM Plex Mono',
      'SFMono-Regular',
      Consolas,
      monospace;
    font-size: 0.84em;
  }

  pre {
    margin: 1.8rem 0;
    padding: 1.15rem 1.25rem;
    overflow-x: auto;

    color: var(--code-ink);
    background: var(--code-bg);
    border: 1px solid var(--rule);
    border-radius: 6px;

    font-family:
      'IBM Plex Mono',
      'SFMono-Regular',
      Consolas,
      monospace;
    font-size: 0.86rem;
    line-height: 1.65;
    white-space: pre-wrap;
  }

  pre code {
    padding: 0;
    background: transparent;
    border: 0;
    border-radius: 0;
    font-size: inherit;
  }

  hr {
    width: 32%;
    margin: 3.25rem auto;
    border: 0;
    border-top: 1px solid var(--rule-strong);
  }

  table {
    width: 100%;
    margin: 2rem 0;
    border-collapse: collapse;
    border-spacing: 0;

    color: var(--ink);
    font-family: 'Hanken Grotesk', system-ui, sans-serif;
    font-size: 0.84rem;
    line-height: 1.55;
  }

  th,
  td {
    padding: 0.7rem 0.75rem;
    text-align: left;
    vertical-align: top;
    border-bottom: 1px solid var(--rule);
  }

  /* The header band is a ROLE too: it was the literal light band, so a prose
     table header came out near-white on the dark sheet with muted ink on it —
     measured 1.61:1, i.e. an unreadable header row in dark mode. */
  th {
    color: var(--ink-soft);
    background: var(--table-head-bg);
    border-top: 1px solid var(--rule-strong);
    border-bottom-color: var(--rule-strong);
    font-weight: 600;
  }

  tr:last-child td {
    border-bottom-color: var(--rule-strong);
  }

  img {
    display: block;
    max-width: 100%;
    height: auto;
    margin: 2rem auto;
    border-radius: 3px;
  }

  .mce-content-body[data-mce-placeholder]::before {
    color: #a39c93;
    font-style: italic;
  }

  @media (max-width: 640px) {
    body {
      padding: 2rem 1.25rem 6rem;
      font-size: 17px;
      line-height: 1.72;
    }

    h1 {
      font-size: 1.7rem;
    }

    h2 {
      font-size: 1.3rem;
    }
  }
`;

export interface MarkdownEditorTransientState {
  bookmark?: unknown;
  scrollY?: number;
}

interface PendingTransientRestore {
  state: MarkdownEditorTransientState;
  expectedMarkdown: string;
  resolve: (restored: boolean) => void;
}

@Component({
  selector: 'app-markdown-editor',
  standalone: true,
  imports: [FormsModule],
  template: ` <textarea id="markdown-tinymce-editor" [class.editor-starting]="editorStarting" [(ngModel)]="htmlContent"></textarea> `,
  styles: [
    `
      :host {
        display: block;
        height: 100%;
        position: relative;
      }

      /* Match the eventual editor surface while TinyMCE is still loading.
         Without this, the native textarea can paint the browser's white
         default for a frame before Oxide replaces it in dark mode. */
      :host > textarea {
        display: block;
        width: 100%;
        height: 100%;
        padding: 0;
        color: var(--editor-ui-text, var(--color-text-main));
        background: var(--editor-ui-bg, var(--bg-surface));
        border: 0;
        outline: 0;
        resize: none;
      }

      /* The textarea is TinyMCE's attachment target, not a second editor.
         Its bound HTML can arrive while the lazy TinyMCE script is still
         loading, so keep the themed startup surface but suppress native text
         until TinyMCE has either initialized or failed. */
      :host > textarea.editor-starting {
        color: transparent;
        -webkit-text-fill-color: transparent;
        caret-color: transparent;
      }

      /* Oxide briefly mounts a throbber over the editing area during init.
         Keep that transient surface on the active app theme as well. */
      :host ::ng-deep .tox .tox-throbber {
        background-color: var(--editor-ui-bg, var(--bg-surface)) !important;
      }

      /* --- Nostos chrome bridge (expert design §3) ---
         One constant skin ('oxide'); every color below is driven by the
         app's --editor-ui-* tokens, which follow the global theme through
         plain CSS inheritance. Theme changes repaint instantly; the editor
         is never destroyed or re-created. */

      /* Kill Oxide container borders, drop shadows, and browser active outlines */
      :host ::ng-deep .tox.tox-tinymce {
        border: none !important;
        box-shadow: none !important;
        background: transparent !important;
        border-radius: 0 !important;
      }

      :host ::ng-deep .tox.tox-tinymce.tox-tinymce--focused,
      :host ::ng-deep .tox.tox-tinymce:focus-within,
      :host ::ng-deep .tox .tox-edit-area,
      :host ::ng-deep .tox .tox-edit-area__iframe {
        border: none !important;
        outline: none !important;
        box-shadow: none !important;
      }

      :host ::ng-deep .tox .tox-edit-area {
        background: transparent !important;
      }

      /* Oxide's focus ring is a PSEUDO-element: .tox .tox-edit-area::before
         paints a 2px solid #006ce7 border and .tox.tox-edit-focus raises its
         opacity to 1 (skins/ui/oxide/skin.css). The border/outline resets on
         .tox-edit-area itself and on the iframe therefore never reach it, so
         the blue rectangle survived every focused state. Opacity - not
         display:none - so the pseudo box stays in the paint order. */
      :host ::ng-deep .tox .tox-edit-area::before {
        opacity: 0 !important;
      }

      :host ::ng-deep .tox .tox-edit-area__iframe {
        background: transparent !important;
      }

      /* Base font and color */
      :host ::ng-deep .tox {
        font-family: 'Hanken Grotesk', system-ui, -apple-system, BlinkMacSystemFont, sans-serif;
        color: var(--editor-ui-text);
      }

      /* Header and toolbar become one quiet, shallow strip */
      :host ::ng-deep .tox .tox-editor-header,
      :host ::ng-deep .tox .tox-toolbar-overlord,
      :host ::ng-deep .tox .tox-toolbar,
      :host ::ng-deep .tox .tox-toolbar__primary {
        background: var(--editor-ui-bg, var(--bg-surface)) !important;
        box-shadow: none !important;
      }

      :host ::ng-deep .tox .tox-editor-header {
        border-bottom: 1px solid var(--border-color) !important;
      }

      :host ::ng-deep .tox .tox-toolbar__primary {
        min-height: 44px;
        max-width: ${STUDIO_MANUSCRIPT_MEASURE_PX}px;
        margin-inline: auto;
        box-sizing: border-box;
        padding-block: 4px !important;
        --studio-toolbar-first-glyph-inset: ${STUDIO_TOOLBAR_FIRST_GLYPH_INSET_PX}px;
        padding-inline-start: calc(${STUDIO_MANUSCRIPT_GUTTER} - var(--studio-toolbar-first-glyph-inset)) !important;
        padding-inline-end: ${STUDIO_MANUSCRIPT_GUTTER} !important;
        flex-wrap: nowrap !important;
        overflow-x: hidden !important;
      }

      /* Remove Oxide's grouped-control appearance */
      :host ::ng-deep .tox .tox-toolbar__group {
        gap: 2px;
        flex-wrap: nowrap !important;
        padding: 0 4px !important;
        border: 0 !important;
      }

      /* Quiet toolbar controls */
      :host ::ng-deep .tox .tox-tbtn,
      :host ::ng-deep .tox .tox-mbtn {
        min-width: 32px;
        height: 32px;
        margin: 0;
        padding: 0 6px;

        color: var(--color-text-muted) !important;
        background: transparent !important;
        border: 0 !important;
        border-radius: var(--radius-sm, 4px) !important;
        box-shadow: none !important;

        transition:
          color 120ms ease,
          background-color 120ms ease;
      }

      :host ::ng-deep .tox .tox-tbtn svg,
      :host ::ng-deep .tox .tox-mbtn svg {
        fill: currentColor !important;
      }

      /* Oxide gives labels/chevrons their own paint. Keep the control as one
         Nostos surface so dropdown labels never look like a second boxed
         control inside the button. */
      :host ::ng-deep .tox .tox-tbtn__select-label,
      :host ::ng-deep .tox .tox-mbtn__select-label,
      :host ::ng-deep .tox .tox-tbtn__text {
        color: inherit !important;
        background: transparent !important;
        box-shadow: none !important;
      }

      /* Hover */
      :host ::ng-deep .tox .tox-tbtn:hover,
      :host ::ng-deep .tox .tox-mbtn:hover,
      :host ::ng-deep .tox .tox-split-button:hover {
        color: var(--color-text-main) !important;
        background: var(--bg-hover) !important;
      }

      /* Active formatting state */
      :host ::ng-deep .tox .tox-tbtn--enabled,
      :host ::ng-deep .tox .tox-tbtn--enabled:hover,
      :host ::ng-deep .tox .tox-mbtn--active {
        color: var(--color-text-main) !important;
        background: var(--color-accent-bg) !important;
      }

      /* Keyboard focus must be clearer than hover */
      :host ::ng-deep .tox .tox-tbtn:focus,
      :host ::ng-deep .tox .tox-mbtn:focus,
      :host ::ng-deep .tox .tox-split-button:focus-within {
        outline: 2px solid var(--focus-ring) !important;
        outline-offset: 1px;
      }

      /* Blocks dropdown is text, not another bulky button */
      :host ::ng-deep .tox .tox-tbtn--select {
        min-width: 112px;
        justify-content: space-between;
      }

      /* Hard removal even if config regresses */
      :host ::ng-deep .tox .tox-menubar,
      :host ::ng-deep .tox .tox-statusbar {
        display: none !important;
      }

      /* --- Phone: one toolbar row, no wrapping, 44px targets ---
         TinyMCE's sliding toolbar mode only engages while the toolbar is a
         single row. The skin's flex-wrap: wrap let the groups wrap to four rows
         on a 390px screen instead, so the sliding overflow button never
         appeared and the whole strip stayed in the document (~124px of chrome
         eating the writing surface). Pinning nowrap hands control back to
         sliding: the excess collapses behind the toolbar's own overflow
         chevron. 44px is the same touch-target contract the dock and Brain
         use. */
      @media (max-width: 768px) {
        :host ::ng-deep .tox .tox-toolbar__primary {
          flex-wrap: nowrap !important;
          overflow-x: hidden !important;
        }

        :host ::ng-deep .tox .tox-toolbar__group {
          flex-wrap: nowrap !important;
        }

        :host ::ng-deep .tox .tox-toolbar__primary .tox-tbtn {
          min-width: 44px !important;
          height: 44px !important;
        }

        /* The sliding overflow row is where most of the formatting controls
           (bold/italic/underline/lists/link/image/clear) actually land on a
           phone, so it needs the same target size — it is a sibling of
           __primary inside the overlay, not a child of it. */
        :host ::ng-deep .tox .tox-toolbar__overflow .tox-tbtn {
          min-width: 44px !important;
          height: 44px !important;
        }

        /* The Blocks label needs the width to stay readable; it was already
           112px on desktop, so widen rather than let it truncate to fit. */
        :host ::ng-deep .tox .tox-toolbar__primary .tox-tbtn--select {
          min-width: 118px !important;
        }
      }

      /* Coarse pointers can occur on a tablet wider than the drawer breakpoint. */
      @media (pointer: coarse) {
        :host ::ng-deep .tox .tox-toolbar__primary {
          --studio-toolbar-first-glyph-inset: 17px;
        }

        :host ::ng-deep .tox .tox-toolbar__primary .tox-tbtn,
        :host ::ng-deep .tox .tox-toolbar__overflow .tox-tbtn {
          min-width: 44px !important;
          height: 44px !important;
        }

        :host ::ng-deep .tox .tox-toolbar__primary .tox-tbtn--select {
          min-width: 118px !important;
        }
      }
    `,
  ],
})
export class MarkdownEditorComponent implements OnInit, OnDestroy {
  initialContent = input<string>('');
  contentChange = output<string>();
  wordCountChange = output<number>();
  /**
   * Typewriter mode: keep the caret line near 45% of the viewport by scrolling
   * the iframe on caret activity. Owned by the studio (toggle + persistence);
   * the editor only follows while this is true.
   */
  typewriter = input<boolean>(false);

  htmlContent = '';
  editorStarting = true;
  private editorId = 'markdown-tinymce-editor';

  private turndownService = new TurndownService({
    headingStyle: 'atx',
    codeBlockStyle: 'fenced',
  });

  private editor: any;
  private editorReady = false;
  private tinyMce: TinyMceApi | null = null;
  private editorInit: Promise<void> | null = null;
  private destroyed = false;
  private lastExternalMarkdownApplied: string | null = null;
  private pendingTransientRestore: PendingTransientRestore | null = null;
  private themeService = inject(ThemeService);
  private tinyMceLoader = inject(TinyMceLoader);

  /**
   * Final chrome (expert design §1): one constant 'oxide' skin, no menubar,
   * no statusbar, no autoresize, sliding toolbar, quickbars for selection.
   * The app theme reaches the chrome through the --editor-ui-* token layer
   * (CSS inheritance), never through skin swapping or editor teardown.
   */
  editorConfig = {
    base_url: '/tinymce',
    suffix: '.min',
    license_key: 'gpl',

    // --- 1. APPEARANCE & LAYOUT ---
    skin: 'oxide',
    content_css: false,
    menubar: false,
    statusbar: false,
    branding: false,
    promotion: false,
    resize: false,

    // --- 2. PLUGINS ---
    plugins: [
      'lists',
      'link',
      'image',
      'table',
      'wordcount',
      'searchreplace',
      'quickbars',
    ].join(' '),

    // --- 3. TOOLBAR ---
    toolbar:
      'undo redo | blocks | bold italic underline | bullist numlist | link image | removeformat',
    toolbar_mode: 'sliding',
    toolbar_sticky: false,

    quickbars_selection_toolbar: 'bold italic | h2 h3 blockquote | link',
    quickbars_insert_toolbar: false,

    contextmenu: false,
    browser_spellcheck: true,

    block_formats:
      'Paragraph=p; Heading 1=h1; Heading 2=h2; Heading 3=h3; Quote=blockquote; Code=pre',

    // --- 4. CONTENT STYLING (warm ink on white paper, theme-independent) ---
    height: '100%',
    content_style: NOSTOS_EDITOR_CONTENT_CSS,

    setup: (editor: any) => {
      this.editor = editor;

      // Apply the iframe theme at TinyMCE PreInit, before content CSS paints.
      // Waiting for init allows one light frame to flash in dark mode.
      editor.on('PreInit', () => this.syncIframeTheme(editor));
      editor.on('Change Undo Redo blur Input', () => this.onHtmlChange(editor.getContent()));
      editor.on('Input KeyUp Click NodeChange', () => this.followCaret(editor));

      const updateWordCount = () => {
        const count = editor.plugins?.wordcount?.body?.getWordCount?.() ?? 0;
        this.wordCountChange.emit(count);
      };

      editor.on('init', () => {
        this.editorReady = true;
        editor.getBody().style.opacity = '1';
        // Re-read the latest theme as init can finish after the user toggles it.
        this.syncIframeTheme(editor);
        this.syncTypewriterLayout(editor);
        // Optional: Safety check in case content loaded before init
        if (this.htmlContent && !editor.getContent()) {
          editor.setContent(this.htmlContent);
        }
        updateWordCount();
        this.tryApplyTransientRestore();
      });
      editor.on('SetContent Change Input Undo Redo', updateWordCount);
    },
  };

  constructor() {
    // Typewriter is an input signal owned by Studio. Keep the iframe layout in
    // sync without recreating TinyMCE when the toggle changes.
    effect(() => {
      this.typewriter();
      this.syncTypewriterLayout();
    });

    // 1. Handle External Content Updates (e.g. clicking a new file in sidebar)
    effect(async () => {
      const markdown = this.initialContent();

      // PARSE FIRST
      // This ensures we have the HTML ready before deciding where to put it
      const html = await marked.parse(markdown);

      // Always update local model (used by template/init)
      this.htmlContent = html;

      // NOW check if editor exists.
      // If editor initialized WHILE we were awaiting parse, this will now be true.
      if (this.editor) {
        // Only update if content is significantly different to avoid cursor jumping
        const currentMarkdown = this.turndownService.turndown(this.editor.getContent());
        if (currentMarkdown.trim() !== markdown.trim()) {
          this.editor.setContent(html);
        }
      }

      this.lastExternalMarkdownApplied = markdown;
      this.tryApplyTransientRestore();
    });

    // 2. Reactively synchronize iframe document with the active theme
    effect(() => {
      this.themeService.theme(); // track theme signal changes
      this.syncIframeTheme();
    });
  }

  ngOnInit() {
    this.editorInit ??= this.initEditor();
  }

  ngOnDestroy() {
    this.destroyed = true;
    this.pendingTransientRestore?.resolve(false);
    this.pendingTransientRestore = null;
    this.destroyEditor();
  }

  private syncTypewriterLayout(editor = this.editor): void {
    if (!editor || !this.editorReady) return;

    try {
      const body = editor.getBody?.();
      const win = editor.getWin?.();
      if (!body?.classList?.toggle) return;

      // Preserve the caret's visible position while the padding changes so
      // toggling the mode itself never causes a disorienting jump.
      const range = editor.selection?.getRng?.();
      const before = caretViewportTop(range);

      body.classList.toggle('nostos-typewriter', this.typewriter());

      const after = caretViewportTop(range);
      if (
        before !== null &&
        after !== null &&
        after !== before &&
        win &&
        typeof win.scrollBy === 'function'
      ) {
        win.scrollBy(0, after - before);
      }

      if (this.typewriter()) {
        queueMicrotask(() => {
          if (this.editor === editor && this.editorReady && this.typewriter()) {
            this.followCaret(editor);
          }
        });
      }
    } catch {
      // Layout sync is best-effort; never make the editor unusable over it.
    }
  }

  private syncIframeTheme(editor = this.editor) {
    if (!editor) return;
    try {
      const doc = editor.getDoc();
      if (!doc) return;
      const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
      if (isDark) {
        doc.documentElement.setAttribute('data-theme', 'dark');
      } else {
        doc.documentElement.removeAttribute('data-theme');
      }
    } catch {
      // Ignored if iframe is not ready or cross-origin
    }
  }

  private async initEditor(): Promise<void> {
    if (this.editor || this.destroyed) return;

    this.editorStarting = true;
    try {
      const tinyMce = await this.tinyMceLoader.load();
      if (this.destroyed || this.editor) return;

      this.tinyMce = tinyMce;
      await Promise.resolve(
        tinyMce.init({
          selector: `#${this.editorId}`,
          ...this.editorConfig,
        }),
      );

      if (this.destroyed && this.editor) {
        tinyMce.remove(this.editor);
        this.editor = null;
      }
    } catch (error) {
      // Keep the lazy loader retryable and restore the native fallback surface
      // if startup fails instead of leaving an invisible textarea behind.
      this.editorInit = null;
      throw error;
    } finally {
      this.editorStarting = false;
    }
  }

  private destroyEditor() {
    this.editorReady = false;
    if (this.editor) {
      // Capture final state before destroying
      const finalHtml = this.editor.getContent();
      this.onHtmlChange(finalHtml);

      // Teardown
      this.tinyMce?.remove(this.editor);
      this.editor = null;
    }
  }

  /**
   * Insert a Markdown snippet at TinyMCE's real current selection/caret.
   *
   * Studio owns what the snippet means (quote/note/reference + provenance);
   * this boundary only converts Markdown, delegates placement to TinyMCE, then
   * emits the resulting full document through the normal HTML -> Markdown path.
   * If the editor is not actually ready, no fallback position is invented.
   */
  async insertMarkdown(markdown: string): Promise<boolean> {
    const snippet = markdown.trim();
    const editor = this.editor;
    if (!snippet || !editor || !this.editorReady) return false;

    const html = await marked.parse(snippet);

    // Parsing may yield after the editor was destroyed or replaced.
    if (!this.editorReady || this.editor !== editor) return false;

    editor.insertContent(html);
    this.onHtmlChange(editor.getContent());
    editor.focus?.();
    return true;
  }

  /**
   * Capture only short-lived editor mechanics. TinyMCE owns selection details;
   * callers never receive DOM nodes or Range objects.
   */
  captureTransientState(): MarkdownEditorTransientState | null {
    const editor = this.editor;
    if (!editor || !this.editorReady) return null;

    const state: MarkdownEditorTransientState = {};

    try {
      const bookmark = editor.selection?.getBookmark?.(2, true);
      const safeBookmark = historySafeClone(bookmark);
      if (safeBookmark !== undefined) state.bookmark = safeBookmark;
    } catch {
      // Exact caret restoration is best-effort.
    }

    try {
      const scrollY = editor.getWin?.()?.scrollY;
      if (typeof scrollY === 'number' && Number.isFinite(scrollY) && scrollY >= 0) {
        state.scrollY = scrollY;
      }
    } catch {
      // Iframe scroll is best-effort.
    }

    return Object.keys(state).length > 0 ? state : null;
  }

  /**
   * Queue a one-shot restore until TinyMCE is ready and the expected Writing
   * content has actually been applied. This avoids restoring a bookmark into a
   * previous/empty document during the Studio handoff.
   */
  restoreTransientState(
    state: MarkdownEditorTransientState,
    expectedMarkdown: string,
  ): Promise<boolean> {
    const bookmark = historySafeClone(state?.bookmark);
    const scrollY =
      typeof state?.scrollY === 'number' && Number.isFinite(state.scrollY) && state.scrollY >= 0
        ? state.scrollY
        : undefined;

    if (bookmark === undefined && scrollY === undefined) return Promise.resolve(false);

    this.pendingTransientRestore?.resolve(false);

    return new Promise<boolean>((resolve) => {
      this.pendingTransientRestore = {
        state: {
          ...(bookmark !== undefined ? { bookmark } : {}),
          ...(scrollY !== undefined ? { scrollY } : {}),
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
    if (
      !pending ||
      !editor ||
      !this.editorReady ||
      this.lastExternalMarkdownApplied !== pending.expectedMarkdown
    ) {
      return;
    }

    this.pendingTransientRestore = null;
    let restored = false;

    if (pending.state.bookmark !== undefined) {
      try {
        if (typeof editor.selection?.moveToBookmark === 'function') {
          editor.selection.moveToBookmark(pending.state.bookmark);
          restored = true;
        }
      } catch {
        // A stale/unsupported TinyMCE bookmark should never block Studio.
      }
    }

    if (pending.state.scrollY !== undefined) {
      try {
        const win = editor.getWin?.();
        if (win && typeof win.scrollTo === 'function') {
          const y = pending.state.scrollY;
          // Selection restoration can move the iframe viewport. Restore scroll
          // one microtask later so the final visible position wins.
          queueMicrotask(() => {
            try {
              win.scrollTo(0, y);
            } catch {
              // Best-effort after the editor may have been torn down.
            }
          });
          restored = true;
        }
      } catch {
        // Missing iframe/window is safe in headless and teardown states.
      }
    }

    pending.resolve(restored);
  }

  onHtmlChange(html: string) {
    const markdown = this.turndownService.turndown(html);
    this.contentChange.emit(markdown);
  }

  /**
   * Typewriter scroll: keep the caret line near 45% of the iframe viewport.
   * TinyMCE selections are usually collapsed ranges while typing, and browsers
   * can report a zero rectangle for those ranges. caretViewportTop() probes an
   * adjacent character/block when needed instead of silently giving up.
   */
  followCaret(editor: any): void {
    if (!this.typewriter()) return;
    try {
      const rectTop = caretViewportTop(editor.selection?.getRng?.());
      const win = editor.getWin?.();
      if (rectTop === null || !win || typeof win.innerHeight !== 'number') return;

      const delta = caretScrollDelta(rectTop, win.innerHeight);
      if (delta !== null && typeof win.scrollBy === 'function') {
        win.scrollBy(0, delta);
      }
    } catch {
      // Caret geometry is best-effort — never break typing over it.
    }
  }
}

/**
 * Resolve the caret's top edge in iframe viewport coordinates.
 *
 * A collapsed DOM Range may expose { top: 0, height: 0 } even when the caret is
 * visibly laid out. In that case, probe the adjacent text character (or nearby
 * inline/block node) with a cloned range so measurement never mutates TinyMCE.
 */
export function caretViewportTop(range: any): number | null {
  if (!range) return null;

  const direct = usableCaretRectTop(range.getBoundingClientRect?.());
  if (direct !== null) return direct;

  if (range.collapsed === false || typeof range.cloneRange !== 'function') return null;

  const container = range.startContainer;
  const offset = Number(range.startOffset);
  if (!container || !Number.isInteger(offset) || offset < 0) return null;

  try {
    if (container.nodeType === 3) {
      const textLength =
        typeof container.length === 'number'
          ? container.length
          : typeof container.data === 'string'
            ? container.data.length
            : 0;

      if (textLength > 0) {
        const probe = range.cloneRange();
        if (offset > 0) {
          probe.setStart(container, Math.min(offset - 1, textLength - 1));
          probe.setEnd(container, Math.min(offset, textLength));
        } else {
          probe.setStart(container, 0);
          probe.setEnd(container, Math.min(1, textLength));
        }

        const probed = usableCaretRectTop(probe.getBoundingClientRect?.());
        if (probed !== null) return probed;
      }
    } else if (container.nodeType === 1) {
      const children = container.childNodes;
      const candidate = children?.[offset] ?? children?.[Math.max(0, offset - 1)];
      const candidateTop = usableCaretRectTop(candidate?.getBoundingClientRect?.());
      if (candidateTop !== null) return candidateTop;
    }

    const element = container.nodeType === 1 ? container : container.parentElement;
    return usableCaretRectTop(element?.getBoundingClientRect?.());
  } catch {
    return null;
  }
}

function usableCaretRectTop(rect: any): number | null {
  const top = Number(rect?.top);
  const height = Number(rect?.height);
  if (!Number.isFinite(top)) return null;

  // The all-zero DOMRect is the common "collapsed caret not laid out" failure.
  if (top === 0 && (!Number.isFinite(height) || height === 0)) return null;

  return top;
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


function historySafeClone(value: unknown): unknown | undefined {
  if (value === undefined) return undefined;

  try {
    const clone = globalThis.structuredClone;
    if (typeof clone === 'function') return clone(value);
  } catch {
    return undefined;
  }

  try {
    return JSON.parse(JSON.stringify(value));
  } catch {
    return undefined;
  }
}
