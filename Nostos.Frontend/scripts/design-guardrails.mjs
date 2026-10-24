/**
 * design-guardrails.mjs — the primitive and copy rules of the UI audit (#1192, #1232).
 *
 * After the audit the app shares one set of primitives and one set of copy rules
 * (`_docs/ui-library.md`, "Copy" and "Design check"). These rules stop new code
 * from quietly going around them. Each finding names the file, the line and the
 * primitive or wording to use instead.
 *
 * Every rule is a pure function of one file's text, so `design-guardrails.test.mjs`
 * can feed it good and bad fixtures. `check-design.mjs` runs `scanTree()` over
 * `src/` as part of `npm run check`.
 *
 * A rule here must have no false positives on the tree. When a hit is genuine
 * drift, fix the code. When it is a justified exception, add it to the narrow,
 * commented allowlist next to the rule, never a broad path glob.
 */
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs';
import { join, relative, extname } from 'node:path';

// ------------------------------------------------------------------ lexing

const lineAt = (text, index) => text.slice(0, index).split('\n').length;

/**
 * Lex TypeScript just enough to tell code from strings and comments.
 *
 * Returns `masked` (the source with every string, comment and regex body blanked
 * to spaces, newlines kept, so indices and lines still line up) and `literals`
 * (each string with its index and its text; a template literal's `${…}` parts
 * become one space-padded placeholder so the words around them stay apart).
 */
export function lexTs(src) {
  const out = src.split('');
  const literals = [];
  const blank = (from, to) => {
    for (let k = from; k < to; k++) if (out[k] !== '\n') out[k] = ' ';
  };
  let i = 0;
  let lastSignificant = '';
  while (i < src.length) {
    const ch = src[i];
    const next = src[i + 1];
    if (ch === '/' && next === '/') {
      const end = src.indexOf('\n', i);
      const stop = end === -1 ? src.length : end;
      blank(i, stop);
      i = stop;
      continue;
    }
    if (ch === '/' && next === '*') {
      const end = src.indexOf('*/', i + 2);
      const stop = end === -1 ? src.length : end + 2;
      blank(i, stop);
      i = stop;
      continue;
    }
    if (ch === '/' && (lastSignificant === '' || /[(,=:[!&|?{};+\-*%<>~^]/.test(lastSignificant))) {
      // A regex literal: skip to its closing slash, honouring classes and escapes.
      let j = i + 1;
      let inClass = false;
      while (j < src.length && src[j] !== '\n') {
        if (src[j] === '\\') { j += 2; continue; }
        if (src[j] === '[') inClass = true;
        else if (src[j] === ']') inClass = false;
        else if (src[j] === '/' && !inClass) break;
        j++;
      }
      blank(i + 1, j);
      i = j + 1;
      lastSignificant = '/';
      continue;
    }
    if (ch === "'" || ch === '"') {
      let j = i + 1;
      let text = '';
      while (j < src.length && src[j] !== ch && src[j] !== '\n') {
        if (src[j] === '\\') { text += src[j + 1] ?? ''; j += 2; continue; }
        text += src[j];
        j++;
      }
      literals.push({ index: i, end: j + 1, quote: ch, text });
      blank(i + 1, j);
      i = j + 1;
      lastSignificant = ch;
      continue;
    }
    if (ch === '`') {
      let j = i + 1;
      let text = '';
      while (j < src.length && src[j] !== '`') {
        if (src[j] === '\\') { text += src[j + 1] ?? ''; j += 2; continue; }
        if (src[j] === '$' && src[j + 1] === '{') {
          let depth = 1;
          let k = j + 2;
          while (k < src.length && depth > 0) {
            if (src[k] === '{') depth++;
            else if (src[k] === '}') depth--;
            k++;
          }
          text += ' \u0001 ';
          j = k;
          continue;
        }
        text += src[j];
        j++;
      }
      literals.push({ index: i, end: j + 1, quote: '`', text });
      blank(i + 1, j);
      i = j + 1;
      lastSignificant = '`';
      continue;
    }
    if (!/\s/.test(ch)) lastSignificant = ch;
    i++;
  }
  return { masked: out.join(''), literals };
}

/** The index of the `)` that closes the `(` at `open`, in masked code. */
function closeParen(masked, open) {
  let depth = 0;
  for (let k = open; k < masked.length; k++) {
    if (masked[k] === '(') depth++;
    else if (masked[k] === ')') { depth--; if (depth === 0) return k; }
  }
  return masked.length;
}

/** `[start, end)` ranges of every call whose callee matches `calleeRe` (ending in `(`). */
function callRanges(masked, calleeRe) {
  const ranges = [];
  for (const m of masked.matchAll(calleeRe)) {
    const open = m.index + m[0].length - 1;
    ranges.push([m.index, closeParen(masked, open)]);
  }
  return ranges;
}

const inRanges = (ranges, index) => ranges.some(([a, b]) => index >= a && index < b);

/**
 * Angular templates in a file: the whole of an `.html`, or each inline
 * `template: \`…\`` of a `.ts`, with the line it starts on.
 */
export function templatesOf(path, raw) {
  if (path.endsWith('.html')) return [{ html: raw, line: 1 }];
  if (!path.endsWith('.ts')) return [];
  const out = [];
  for (const m of raw.matchAll(/\btemplate\s*:\s*`/g)) {
    const start = m.index + m[0].length;
    let j = start;
    while (j < raw.length && raw[j] !== '`') j += raw[j] === '\\' ? 2 : 1;
    out.push({ html: raw.slice(start, j), line: lineAt(raw, start) });
  }
  return out;
}

/** Inline component CSS of a `.ts` (`styles: [\`…\`]` or `styles: \`…\``). */
export function inlineStylesOf(raw) {
  const out = [];
  for (const m of raw.matchAll(/\bstyles\s*:\s*\[?\s*`/g)) {
    const start = m.index + m[0].length;
    let j = start;
    while (j < raw.length && raw[j] !== '`') j += raw[j] === '\\' ? 2 : 1;
    out.push({ css: raw.slice(start, j), line: lineAt(raw, start) });
  }
  return out;
}

const blankKeepLines = (s) => s.replace(/[^\n]/g, ' ');

/**
 * Scan an Angular template into tags and text, with indices into `html`.
 *
 * Comments are dropped; interpolations `{{ … }}` are kept out of the text (their
 * string literals are reported separately as `literals`, because
 * `{{ x || 'Unknown author' }}` is copy too) and leave a `\u0001` behind so the
 * words around them are never read as one label.
 */
export function scanTemplate(html) {
  const src = html.replace(/<!--[\s\S]*?-->/g, blankKeepLines);
  const tags = [];
  const texts = [];
  const literals = [];
  const pushExprLiterals = (expr, base, context) => {
    for (const lit of lexTs(expr).literals) {
      literals.push({ text: lit.text, index: base + lit.index, context });
    }
  };

  let i = 0;
  let textStart = 0;
  let text = '';
  const flushText = () => {
    if (text.trim()) texts.push({ text, index: textStart });
    text = '';
  };
  while (i < src.length) {
    if (src.startsWith('{{', i)) {
      const end = src.indexOf('}}', i + 2);
      const stop = end === -1 ? src.length : end + 2;
      pushExprLiterals(src.slice(i + 2, stop - 2), i + 2, 'interpolation');
      if (!text) textStart = i;
      text += '\u0001';
      i = stop;
      continue;
    }
    if (src[i] === '<' && /[a-zA-Z/]/.test(src[i + 1] ?? '')) {
      flushText();
      // A tag: read to the unquoted `>`.
      let j = i + 1;
      let quote = null;
      while (j < src.length) {
        const c = src[j];
        if (quote) { if (c === quote) quote = null; }
        else if (c === '"' || c === "'") quote = c;
        else if (c === '>') break;
        j++;
      }
      const body = src.slice(i, j + 1);
      const closing = body.startsWith('</');
      const name = (body.match(/^<\/?([a-zA-Z][\w-]*)/) ?? [])[1] ?? '';
      const attrs = [];
      if (!closing) {
        const attrRe = /([^\s=>/"']+)(?:\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+)))?/g;
        attrRe.lastIndex = name.length + 1;
        let m;
        while ((m = attrRe.exec(body))) {
          const value = m[3] ?? m[4] ?? m[5] ?? null;
          const valueIndex = value === null ? -1 : i + m.index + m[0].indexOf(value, m[1].length);
          attrs.push({ name: m[1], value, index: i + m.index, valueIndex });
        }
      }
      tags.push({ name: name.toLowerCase(), closing, index: i, end: j + 1, raw: body, attrs });
      i = j + 1;
      textStart = i;
      continue;
    }
    if (!text) textStart = i;
    text += src[i];
    i++;
  }
  flushText();

  // Control flow (`@if (…) {`, `} @else {`, `@for (…; track …) {`, `@let x = …;`)
  // sits in text positions but is code: blank it, keeping its literals.
  for (const t of texts) {
    let s = t.text;
    let cleaned = '';
    let k = 0;
    while (k < s.length) {
      const m = /^@(else\s+if|if|else|for|switch|case|default|empty|defer|placeholder|loading|error|let)\b/.exec(s.slice(k));
      if (m) {
        let j = k + m[0].length;
        if (m[1] === 'let') {
          const end = s.indexOf(';', j);
          const stop = end === -1 ? s.length : end + 1;
          pushExprLiterals(s.slice(j, stop), t.index + j, 'control-flow');
          cleaned += '\u0001';
          k = stop;
          continue;
        }
        while (j < s.length && /\s/.test(s[j])) j++;
        if (s[j] === '(') {
          let depth = 0;
          const open = j;
          for (; j < s.length; j++) {
            if (s[j] === '(') depth++;
            else if (s[j] === ')') { depth--; if (depth === 0) { j++; break; } }
          }
          pushExprLiterals(s.slice(open, j), t.index + open, 'control-flow');
        }
        cleaned += '\u0001';
        k = j;
        continue;
      }
      if (s[k] === '{' || s[k] === '}') { cleaned += '\u0001'; k++; continue; }
      cleaned += s[k];
      k++;
    }
    t.text = cleaned;
  }

  for (const tag of tags) {
    for (const a of tag.attrs) {
      if (a.value === null) continue;
      const bound = /^\[|^\(|^\*|^\[\(/.test(a.name);
      if (bound) pushExprLiterals(a.value, a.valueIndex, a.name);
    }
  }
  return { tags, texts: texts.filter((t) => t.text.replace(/\u0001/g, '').trim()), literals, src };
}

// ------------------------------------------------------------- copy helpers

/**
 * Attributes whose value a reader sees or hears. Bound forms (`[label]`,
 * `[attr.aria-label]`) count too, through their string literals.
 */
const USER_ATTR = /^(?:\[(?:attr\.)?)?(aria-label|aria-description|aria-valuetext|aria-roledescription|title|placeholder|alt|label|message|detail|heading|subtitle|description|hint|caption|tooltip|valueText|ariaLabel|[a-z]+(?:Label|Message|Text|Title|Heading|Placeholder|Hint|Detail))\]?$/;

/**
 * TS object keys and signals whose strings are copy. Keys are matched before a
 * `:`, signal names before `.set(`.
 */
const USER_KEY = /\b(label|title|message|heading|detail|placeholder|ariaLabel|description|hint|caption|subtitle|confirmLabel|cancelLabel|confirmText|actionLabel|emptyMessage|failedMessage|body)\s*:\s*$/;
const USER_SIGNAL = /\b\w*(?:error|message|notice|status|label)\w*\s*\.\s*set\s*\(\s*$/i;

/**
 * Proper nouns and names that keep their capitals inside a sentence-case label.
 * A capitalised word is only Title Case drift when it is not one of these.
 */
export const PROPER_NOUNS = new Set([
  // Nostos and its named places.
  'Nostos', 'Brain', 'Studio', 'Library', 'Settings', 'Reader', 'Cloud', 'Map',
  // Themes and fonts.
  'Light', 'Sepia', 'Mist', 'Dark', 'Study', 'Writer’s', "Writer's",
  'Literata', 'Lora', 'Merriweather', 'Georgia', 'Inter', 'Atkinson', 'Hyperlegible',
  'Bookerly', 'Charter', 'Palatino', 'Garamond', 'Baskerville', 'Crimson', 'Pro', 'Text',
  'Source', 'Serif', 'Sans', 'Iowan', 'Old', 'Style', 'System', 'OpenDyslexic', 'Lexend',
  // Services, formats and products named in the UI.
  'Google', 'Books', 'Open', 'Calibre', 'Kobo', 'Kindle', 'Readwise', 'Markdown', 'Zotero',
  'Audiobookshelf', 'Hardcover', 'Goodreads', 'StoryGraph', 'Apple', 'Anthropic', 'Claude',
  'OpenAI', 'Ollama', 'Gemini', 'Mistral', 'Groq', 'OpenRouter', 'LibriVox', 'Gutenberg',
  'Project', 'Standard', 'Ebooks', 'Anna’s', "Anna's", 'Archive', 'Internet', 'Word', 'Docs',
  'Microsoft', 'Unraid', 'CasaOS', 'Umbrel', 'Runtipi', 'Docker', 'Paddle', 'Neon',
  'Chrome', 'Safari', 'Firefox', 'Edge', 'Android', 'iOS', 'Windows', 'Linux', 'Mac',
  'BibTeX', 'Chicago', 'Harvard', 'Vancouver', 'Turabian',
  // Days and months.
  'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday',
  'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August',
  'September', 'October', 'November', 'December',
  'Jan', 'Feb', 'Mar', 'Apr', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
  // Languages.
  'English', 'Swedish', 'German', 'French', 'Spanish', 'Italian',
  // Keys, named the way the keyboard prints them ("Press Enter").
  'Enter', 'Return', 'Escape', 'Esc', 'Shift', 'Tab', 'Ctrl', 'Cmd', 'Alt', 'Option', 'Backspace',
  'Substack',
]);

/** Proper names of more than one word, cleared before the word check. */
export const PROPER_PHRASES = ['Ask Nostos', 'Works Cited', 'Open Library', 'Google Books',
  'Apple Books', 'Project Gutenberg', 'Standard Ebooks', 'Internet Archive', 'Anna’s Archive',
  "Anna's Archive", 'Writer’s Study', "Writer's Study", 'Nostos Cloud',
  // A Settings page named inside a label: "Open Backups & data".
  'Backups & data',
  // Kindle's own file name.
  'My Clippings',
  // Studio typefaces (writing-studio/page-settings/studio-preferences.service.ts).
  'Libre Baskerville', 'Crimson Pro', 'Source Serif', 'Hanken Grotesk', 'Special Elite'];

/** The few HTML entities copy uses, so `&amp;` reads as the `&` a reader sees. */
const decodeEntities = (s) => s
  .replace(/&amp;/g, '&').replace(/&nbsp;/g, ' ').replace(/&hellip;/g, '…')
  .replace(/&rsquo;/g, '’').replace(/&lsquo;/g, '‘').replace(/&ldquo;/g, '“').replace(/&rdquo;/g, '”')
  .replace(/&mdash;/g, '—').replace(/&ndash;/g, '–').replace(/&middot;/g, '·');

/**
 * Is this label written in Title Case? True when two capitalised words sit side
 * by side and the second is neither a sentence start nor a proper noun:
 * "Add Book" is drift, "Open in Brain" and "Ask Nostos" are not.
 */
export function titleCaseWords(text) {
  let s = decodeEntities(text);
  // An example value ("e.g. Simone Weil") is a name, not a label.
  if (/^\s*e\.g\./.test(s)) return null;
  for (const p of PROPER_PHRASES) s = s.split(p).join('\u0001');
  const words = [...s.matchAll(/[A-Za-z][A-Za-z'’]*/g)];
  for (let w = 1; w < words.length; w++) {
    const prev = words[w - 1];
    const cur = words[w];
    const between = s.slice(prev.index + prev[0].length, cur.index);
    if (between !== ' ') continue; // only words joined by one plain space
    const cap = (x) => /^[A-Z][a-z]/.test(x);
    if (!cap(prev[0]) || !cap(cur[0])) continue;
    if (PROPER_NOUNS.has(cur[0])) continue;
    return `${prev[0]} ${cur[0]}`;
  }
  return null;
}

// Capital R: lower-case `'retry'` is an action key in code, not a label.
const RETRY = /^\s*Retry\s*$/;
const ASCII_ELLIPSIS = /\.\.\./;
const COULD_NOT = /\b(Failed to|Unable to|Couldn['’]t)\b/;

/**
 * The copy rules for one string a reader sees. `kind` limits Title Case to
 * labels (a whole attribute value, a text node, a key's value), not to prose
 * read out of an expression.
 */
function copyFindings(text, { titleCase = true } = {}) {
  const out = [];
  const plain = decodeEntities(text.replace(/\u0001/g, ' '));
  if (RETRY.test(plain)) {
    out.push(['copy-retry', 'The retry label is "Try again" (TRY_AGAIN), not "Retry".']);
  }
  if (ASCII_ELLIPSIS.test(plain)) {
    out.push(['copy-ellipsis', `Three dots in "${plain.trim().slice(0, 50)}": write the ellipsis character "…".`]);
  }
  const fail = plain.match(COULD_NOT);
  if (fail) {
    out.push(['copy-could-not',
      `"${fail[1]}" in "${plain.trim().slice(0, 60)}": failures start "Could not …".`]);
  }
  if (titleCase) {
    const pair = titleCaseWords(text);
    if (pair) {
      out.push(['copy-title-case',
        `"${pair}" in "${plain.trim().slice(0, 60)}" is Title Case: write sentence case, ` +
          'or add a proper noun to PROPER_NOUNS in scripts/design-guardrails.mjs.']);
    }
  }
  return out;
}

// ------------------------------------------------------- canonical labels

/** Where the canonical wordings live; these files may spell them. */
const CANONICAL_LABEL_HOMES = new Set([
  'src/app/core/reading-status.ts',
  'src/app/core/book-format.ts',
]);

/**
 * Status and author wordings that have one home. Title Case variants of the old
 * reading statuses fail anywhere; the sentence-case labels fail as a whole
 * hard-coded string outside their home.
 */
function canonicalLabelFindings(text) {
  const plain = text.replace(/\u0001/g, ' ');
  const out = [];
  const old = plain.match(/\b(In Progress|Not Started|To Read)\b/);
  if (old) {
    out.push(['reading-status-label',
      `"${old[1]}" is an old reading-status label: use READING_STATUS_LABEL / TO_READ_LABEL ` +
        'from core/reading-status.ts (Not started, Reading, Finished, To read).']);
  } else if (/^\s*(not started|to read)\s*$/i.test(plain)) {
    out.push(['reading-status-label',
      `"${plain.trim()}" is hard-coded: use READING_STATUS_LABEL / TO_READ_LABEL from core/reading-status.ts.`]);
  }
  if (/^\s*unknown author\s*$/i.test(plain)) {
    out.push(['unknown-author-label', '"Unknown author" is hard-coded: use UNKNOWN_AUTHOR from core/book-format.ts.']);
  }
  return out;
}

// ------------------------------------------------------------------ dates

/** The formatter and its pipe are the only places allowed to format a date. */
const DATE_HOMES = new Set(['src/app/core/date-formats.ts', 'src/app/ui/pipes/nostos-date.pipe.ts']);

const DATE_MESSAGE = 'Format dates with the nostosDate pipe or formatNostosDate() (core/date-formats.ts).';

/** Date formatting in TypeScript code (strings and comments are already masked). */
function dateFindingsTs(masked) {
  const out = [];
  const patterns = [
    [/\bDatePipe\b/g, 'DatePipe'],
    [/\bformatDate\s*\(/g, 'formatDate()'],
    [/\.toLocale(?:Date|Time)String\s*\(/g, 'toLocaleDateString()/toLocaleTimeString()'],
    [/\bIntl\s*\.\s*DateTimeFormat\b/g, 'Intl.DateTimeFormat'],
  ];
  for (const [re, what] of patterns) {
    for (const m of masked.matchAll(re)) out.push({ index: m.index, what });
  }
  // `toLocaleString()` also formats numbers ("12,400 words"), which is fine. It is a
  // date only when its receiver is one: `new Date(…)`, or a name that says so.
  for (const m of masked.matchAll(/\.toLocaleString\s*\(/g)) {
    const before = masked.slice(Math.max(0, m.index - 80), m.index);
    if (/(?:new\s+Date\s*\([^()]*\)|\b\w*(?:[dD]ate|[tT]ime|At|Utc|When)\w*(?:\(\))?)\s*\)?\s*$/.test(before)) {
      out.push({ index: m.index, what: 'toLocaleString() on a date' });
    }
  }
  return out;
}

// ----------------------------------------------------- hand-built states

/** The primitives that own the states; their own CSS may draw them. */
const STATE_HOMES = new Set([
  'src/app/ui/empty-state/empty-state.component.ts',
  'src/app/ui/loading-indicator/loading-indicator.component.ts',
  'src/app/ui/paging-status/paging-status.component.ts',
  'src/app/ui/button/button.component.ts',
  'src/app/ui/progress/progress.component.ts',
]);

/**
 * Keyframes that are product motion, not a loading state. Each one is named here
 * with its reason so a new spinner or skeleton cannot hide behind a pattern.
 */
const PRODUCT_KEYFRAMES = new Map([
  // The assistant's "Thinking…" line: a text shimmer while a long answer is
  // written, deliberately text rather than a spinner (#1003).
  ['assistant-shimmer', 'src/app/ui/assistant/assistant.component.css'],
  ['assistant-thinking', 'src/app/ui/assistant/assistant.component.css'],
  // Read-aloud buffering: the transport's play glyph breathes while audio
  // streams in, part of the transport rather than a page load.
  ['read-aloud-buffering', 'src/app/reader/read-aloud/read-aloud-bar.component.css'],
  ['read-aloud-buffering-reveal', 'src/app/reader/read-aloud/read-aloud-bar.component.css'],
]);

const STATE_KEYFRAME = /@keyframes\s+([\w-]*(?:skeleton|pulse|shimmer|spin|spinner|loading|placeholder-wave|buffering|thinking)[\w-]*)/gi;

/** Classes a template puts on a `<nostos-empty-state>` host to place it. */
function emptyStateHostClasses(templates) {
  const out = new Set();
  for (const html of templates) {
    for (const m of html.matchAll(/<nostos-empty-state\b[^>]*?\bclass\s*=\s*"([^"]*)"/g)) {
      for (const c of m[1].split(/\s+/)) if (c) out.add(c);
    }
  }
  return out;
}

function stateFindingsCss(css, relPath, hostClasses = new Set()) {
  const out = [];
  if (STATE_HOMES.has(relPath)) return out;
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, blankKeepLines);
  for (const m of clean.matchAll(/\.([\w-]*empty-state[\w-]*)/g)) {
    if (m[1].startsWith('nostos-empty-state')) continue;
    // Placing the primitive with a class on its host is how it is meant to be used.
    if (hostClasses.has(m[1])) continue;
    out.push({ index: m.index, rule: 'hand-built-empty-state',
      message: `.${m[1]} styles a hand-built empty state: use <nostos-empty-state>.` });
  }
  for (const m of clean.matchAll(/\.([\w-]*(?:spinner|skeleton)[\w-]*)\s*[,{:.\s]/g)) {
    out.push({ index: m.index, rule: 'hand-built-loading',
      message: `.${m[1]} is a local spinner or skeleton: use <nostos-loading-indicator>.` });
  }
  for (const m of clean.matchAll(STATE_KEYFRAME)) {
    const home = PRODUCT_KEYFRAMES.get(m[1]);
    const inlineHome = home && relPath === home.replace(/\.css$/, '.ts');
    if (home === relPath || inlineHome) continue;
    out.push({ index: m.index, rule: 'hand-built-loading',
      message: `@keyframes ${m[1]} draws a loading state: use <nostos-loading-indicator>.` });
  }
  return out;
}

function stateFindingsTemplate(scan, relPath) {
  const out = [];
  if (STATE_HOMES.has(relPath)) return out;
  for (const tag of scan.tags) {
    if (tag.closing || tag.name === 'nostos-empty-state') continue;
    const cls = tag.attrs.find((a) => a.name === 'class')?.value ?? '';
    const hit = cls.split(/\s+/).find((c) => /empty-state/.test(c));
    if (hit) {
      out.push({ index: tag.index, rule: 'hand-built-empty-state',
        message: `<${tag.name} class="${hit}"> is a hand-built empty state: use <nostos-empty-state>.` });
    }
    const spin = cls.split(/\s+/).find((c) => /(?:^|-)(?:spinner|skeleton)(?:-|$)/.test(c));
    if (spin && tag.name !== 'nostos-loading-indicator') {
      out.push({ index: tag.index, rule: 'hand-built-loading',
        message: `<${tag.name} class="${spin}"> is a local spinner or skeleton: use <nostos-loading-indicator>.` });
    }
  }
  for (const t of scan.texts) {
    // "Loading…" as the whole text of a state. A spinner right before it (an
    // overlay that shows both) makes it the spinner's caption, which is fine.
    if (!/^\s*Loading\b[^\u0001]*(…|\.\.\.)\s*$/.test(t.text)) continue;
    const before = scan.src.slice(Math.max(0, t.index - 200), t.index);
    if (/<nostos-loading-indicator\b[^>]*>(?:\s*<\/nostos-loading-indicator>)?\s*(?:<[a-z][^>]*>\s*)?$/.test(before)) continue;
    {
      out.push({ index: t.index, rule: 'hand-built-loading',
        message: `"${t.text.trim()}" as page text is a hand-built loading state: ` +
          'use <nostos-loading-indicator> (its label is announced, not shown).' });
    }
  }
  return out;
}

// ------------------------------------------------------------ raw buttons

/** Directives that make a `<button>` a primitive. */
export const BUTTON_PRIMITIVES = ['appButton', 'appIconButton', 'appChip', 'appChoiceRow',
  'appTopicOption', 'appIconTile', 'nostosMenuItem', 'nostosMenuItemRadio', 'nostosMenuStepItem'];

/**
 * Raw buttons that keep their own styling, by template and class, each with its
 * reason. These are product controls whose interaction, geometry or ARIA is the
 * product surface (see "Start here" in _docs/ui-library.md), or a primitive's own
 * insides. Anything else that styles a raw `<button>` locally should use a primitive.
 */
export const PRODUCT_BUTTONS = new Map(Object.entries({
  // --- Primitives' own insides: the primitive IS the styled button.
  'src/app/ui/dropdown/dropdown.component.ts': ['nostos-dropdown__trigger'],
  'src/app/ui/edition-switcher/edition-switcher.component.ts': ['es-trigger'],
  'src/app/ui/pen-swatches/pen-swatches.component.ts': ['pen-swatch'],
  'src/app/ui/segmented/segmented.component.ts': ['seg-opt'],
  'src/app/ui/theme-picker/theme-picker.component.ts': ['tp-swatch'],
  'src/app/ui/topic-chip/topic-chip.component.ts': ['topic-chip-name'],
  'src/app/ui/view-toggle/view-toggle.component.ts': ['vt-opt'],
  // The tree's disclosure triangle is part of its treeitem keyboard contract.
  'src/app/ui/flat-tree/flat-tree.component.html': ['toggle-btn'],
  // The editor's selection bubble: formatting marks over the text, owned by the editor.
  'src/app/ui/markdown-editor/markdown-editor.component.html': ['editor-bubble-btn'],
  // NoteItem's footer: the time, "Cited in", "Show in book" and the topic add are
  // quiet inline text in the meta line, next to plain <a> twins with the same look.
  'src/app/ui/note-item/note-item.component.html': ['note-item-add', 'note-item-time', 'note-item-cited', 'note-item-source'],
  // An imported book in the import report: the same row as the non-interactive
  // failure rows beside it, made clickable.
  'src/app/ui/highlight-import-modal/highlight-import-modal.component.html': ['highlight-row'],
  // Assistant rows and recording controls (see also check-design RULE 15).
  'src/app/ui/assistant/assistant.component.html': ['starter', 'suggestion-chip', 'anchor-chip-dismiss', 'voice-control', 'assistant-trigger'],

  // --- App chrome and navigation.
  // The dock: navigation rows whose <a> twins share the recipe.
  'src/app/layout/app-dock/app-dock.component.ts': ['dock-item', 'dock-ask'],
  // Side navigation rows (the shared `.nav-item` page-chrome recipe in styles.css).
  'src/app/library/sidebar-collections/sidebar-collections.component.html': ['nav-item'],
  'src/app/settings/settings.component.html': ['settings-nav-item', 'theme-card'],

  // --- Transport: play, skip, chapter and time are the player's own controls.
  'src/app/reader/audio-reader/audio-reader.component.html': ['chapter-btn', 'time-label-btn', 'skip-btn', 'play-btn'],
  'src/app/reader/read-aloud/read-aloud-bar.component.html': ['read-aloud-toggle'],

  // --- Acquisition, Brain and Studio product rows (also in check-design RULE 15).
  'src/app/add-book-modal/add-book-modal.component.html': ['source-result'],
  'src/app/brain/brain-topic-words.component.html': ['words-add'],
  'src/app/writing-studio/writing-studio.component.html': ['editor-desk-telemetry', 'tab-btn', 'toggle-opt', 'list-item'],
  // Typeface and citation-style pickers are role="radio" rows set in their own face.
  'src/app/writing-studio/page-settings/studio-page-settings.component.html': ['ps-font', 'ps-typeface'],
  // The source palette is keyboard-driven: "Browse reference" is a link in its
  // empty line, and the two insert actions are mouse shortcuts (tabindex -1)
  // for what Enter and Shift+Enter already do.
  'src/app/writing-studio/source-palette/source-palette.component.html': ['sp-browse', 'sp-more-action'],
}));

/** Class selectors a stylesheet defines. */
function classSelectors(css) {
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, ' ');
  const out = new Set();
  // Only selector text, not declarations (`url(x.png)` is not a class).
  for (const block of clean.matchAll(/([^{}]+)\{/g)) {
    for (const m of block[1].matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)) out.add(m[1]);
  }
  return out;
}

function rawButtonFindings(scan, relPath, componentClasses) {
  const out = [];
  const allowed = PRODUCT_BUTTONS.get(relPath) ?? [];
  for (const tag of scan.tags) {
    if (tag.closing || tag.name !== 'button') continue;
    const names = tag.attrs.map((a) => a.name.replace(/^\[|\]$/g, ''));
    if (names.some((n) => BUTTON_PRIMITIVES.includes(n))) continue;
    const classes = [
      ...(tag.attrs.find((a) => a.name === 'class')?.value ?? '').split(/\s+/),
      ...tag.attrs.filter((a) => /^\[class\.[\w-]+\]$/.test(a.name)).map((a) => a.name.slice(7, -1)),
    ].filter(Boolean);
    const styled = classes.filter((c) => componentClasses.has(c));
    if (!styled.length) continue;
    if (styled.some((c) => allowed.includes(c))) continue;
    out.push({ index: tag.index, rule: 'raw-button',
      message: `<button class="${styled.join(' ')}"> is styled locally without a primitive: ` +
        'use appButton, appIconButton, appChip, appChoiceRow or nostosMenuItem, or list it in ' +
        'PRODUCT_BUTTONS with the reason it is a product control.' });
  }
  return out;
}

// ------------------------------------------------------- other primitives

/** Primitives' own files may draw what they own. */
const PRIMITIVE_HOMES = {
  range: 'src/app/ui/range/',
  search: 'src/app/ui/search-field/',
  cover: 'src/app/ui/book-cover/',
  pens: 'src/app/ui/pen-swatches/',
};

function primitiveFindingsTemplate(scan, relPath) {
  const out = [];
  const opens = [];
  for (const tag of scan.tags) {
    if (tag.name === 'nostos-search-field') {
      if (tag.closing) opens.pop(); else opens.push(tag.index);
      continue;
    }
    if (tag.closing) continue;
    const attr = (n) => tag.attrs.find((a) => a.name === n);
    const type = attr('type')?.value?.toLowerCase();
    if (tag.name === 'input' && type === 'range' && !attr('appRange') && !relPath.startsWith(PRIMITIVE_HOMES.range)) {
      out.push({ index: tag.index, rule: 'raw-range',
        message: '<input type="range"> without appRange: add appRange, the one slider.' });
    }
    if (tag.name === 'input' && type === 'search' && !opens.length && !relPath.startsWith(PRIMITIVE_HOMES.search)) {
      out.push({ index: tag.index, rule: 'raw-search-field',
        message: '<input type="search"> outside <nostos-search-field>: wrap it in the search field.' });
    }
    const cls = attr('class')?.value ?? '';
    const cover = cls.split(/\s+/).find((c) => /cover[\w-]*(?:placeholder|fallback|empty|missing)|(?:placeholder|fallback|no)-cover/.test(c));
    if (cover && !relPath.startsWith(PRIMITIVE_HOMES.cover)) {
      out.push({ index: tag.index, rule: 'hand-built-cover',
        message: `class="${cover}" draws a cover placeholder: use <nostos-book-cover>, which owns it.` });
    }
    const pen = cls.split(/\s+/).find((c) => /(?:pen|colou?r|highlight)-(?:dot|swatch)/.test(c));
    if (pen && !relPath.startsWith(PRIMITIVE_HOMES.pens)) {
      out.push({ index: tag.index, rule: 'hand-built-pens',
        message: `class="${pen}" draws pen dots: use <app-pen-swatches>.` });
    }
  }
  return out;
}

function primitiveFindingsCss(css, relPath) {
  const out = [];
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, blankKeepLines);
  if (!relPath.startsWith(PRIMITIVE_HOMES.cover)) {
    for (const m of clean.matchAll(/\.([\w-]*(?:cover[\w-]*(?:placeholder|fallback|empty|missing)|(?:placeholder|fallback|no)-cover)[\w-]*)/g)) {
      out.push({ index: m.index, rule: 'hand-built-cover',
        message: `.${m[1]} styles a cover placeholder: use <nostos-book-cover>.` });
    }
  }
  if (!relPath.startsWith(PRIMITIVE_HOMES.pens)) {
    for (const m of clean.matchAll(/\.([\w-]*(?:pen|colou?r|highlight)-(?:dot|swatch)[\w-]*)/g)) {
      out.push({ index: m.index, rule: 'hand-built-pens',
        message: `.${m[1]} styles pen dots: use <app-pen-swatches>.` });
    }
  }
  return out;
}

// The phone breakpoint is 768 (`ui/breakpoints.ts`); 480 is "very small phone" and
// 900 is the Studio's compact layout. An overlay (sheet, drawer, popover, palette)
// that switches layout at any other width drifts from the rest, so the sheets
// stop agreeing on what a phone is. Only overlay stylesheets are checked: a page
// layout may break wherever its content needs.
const OVERLAY_STYLE_HOMES = [
  'src/app/ui/modal-shell/', 'src/app/ui/settings-popover/', 'src/app/ui/menu/',
  'src/app/ui/dropdown/', 'src/app/ui/command-palette/', 'src/app/ui/assistant/',
  'src/app/ui/topic-picker-popover/', 'src/app/ui/topic-autocomplete-panel/',
  'src/app/ui/confirm-modal/', 'src/app/ui/toast-container/',
  'src/app/writing-studio/source-palette/', 'src/app/writing-studio/page-settings/',
  'src/app/library/sidebar-collections/',
  'src/app/brain/brain-shell.',
];
const PHONE_WIDTHS = new Set([480, 768, 900]);

function breakpointFindingsCss(css, relPath) {
  if (!OVERLAY_STYLE_HOMES.some((home) => relPath.startsWith(home))) return [];
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, blankKeepLines);
  const out = [];
  for (const m of clean.matchAll(/@media[^{]*?\(\s*max-width\s*:\s*(\d+(?:\.\d+)?)px\s*\)/g)) {
    const width = Number(m[1]);
    // Narrower than a phone or wider than a tablet is not a phone breakpoint.
    if (width < 320 || width > 1024 || PHONE_WIDTHS.has(width)) continue;
    out.push({ index: m.index, rule: 'phone-breakpoint',
      message: `max-width: ${width}px is not a phone breakpoint: use 768px (PHONE_MAX in ui/breakpoints.ts), or 480px / 900px.` });
  }
  return out;
}

function primitiveFindingsTs(masked, relPath) {
  const out = [];
  // Rendering the pens as a list is the pen-swatches component's job; other code
  // works with one colour (HighlightColour) at a time.
  if (/\.component\.ts$/.test(relPath) && !relPath.startsWith(PRIMITIVE_HOMES.pens)) {
    for (const m of masked.matchAll(/\bHIGHLIGHT_COLOURS\b/g)) {
      out.push({ index: m.index, rule: 'hand-built-pens',
        message: 'A component lists HIGHLIGHT_COLOURS itself: use <app-pen-swatches>.' });
    }
  }
  return out;
}

// ------------------------------------------------------------- per file

/**
 * A narrow, written exception: a comment on the finding's line, or the comment
 * block directly above it, that says `design-allow <rule>: <reason>`.
 *
 *   <!-- design-allow copy-title-case: a book title as the example value -->
 *   // design-allow hand-built-loading: a one-line status inside a shown result
 *
 * The reason is required, so an exception always says why it is one.
 */
export function allowedByMarker(lines, line, rule) {
  const says = (text) => {
    const m = /design-allow\s+([\w, -]+?):\s*\S/.exec(text);
    return !!m && m[1].split(/[\s,]+/).includes(rule);
  };
  if (says(lines[line - 1] ?? '')) return true;
  let i = line - 2;
  const above = (lines[i] ?? '').trim();
  if (above.endsWith('-->') || above.endsWith('*/')) {
    const open = above.endsWith('-->') ? '<!--' : '/*';
    for (; i >= 0 && i > line - 14; i--) {
      if (says(lines[i])) return true;
      if (lines[i].includes(open)) return false;
    }
    return false;
  }
  for (; i >= 0 && (lines[i] ?? '').trim().startsWith('//'); i--) if (says(lines[i])) return true;
  return false;
}

/**
 * Every guardrail finding for one file. `relPath` is relative to the frontend
 * root (`src/app/...`). `context` is what the file renders with:
 * `componentCss` (the template's own stylesheet and inline styles, for the
 * raw-button rule) and `componentTemplates` (for a stylesheet, the templates it
 * styles, so a class that places a `<nostos-empty-state>` is not read as a copy).
 */
export function checkFile(relPath, raw, context = {}) {
  const { componentCss = '', componentTemplates = [] } = context;
  const lines = raw.split('\n');
  const findings = [];
  const add = (rule, index, base, text, message) => {
    const line = lineAt(text, index) + base - 1;
    if (allowedByMarker(lines, line, rule)) return;
    findings.push({ rule, file: relPath, line, message });
  };

  if (relPath.endsWith('.spec.ts') || relPath.endsWith('.d.ts')) return findings;
  const hostClasses = emptyStateHostClasses(
    [...componentTemplates, ...templatesOf(relPath, raw).map((t) => t.html)]);

  if (relPath.endsWith('.css')) {
    for (const f of [...stateFindingsCss(raw, relPath, hostClasses), ...primitiveFindingsCss(raw, relPath), ...breakpointFindingsCss(raw, relPath)]) {
      add(f.rule, f.index, 1, raw, f.message);
    }
    return findings;
  }

  const componentClasses = classSelectors(componentCss);

  // Templates: the .html, or each inline template of a .ts.
  for (const { html, line } of templatesOf(relPath, raw)) {
    const scan = scanTemplate(html);
    const at = (rule, index, message) => add(rule, index, line, html, message);
    {
      for (const t of scan.texts) {
        for (const [rule, msg] of [...copyFindings(t.text), ...canonicalLabelFindings(t.text)]) {
          at(rule, t.index + (t.text.length - t.text.trimStart().length), msg);
        }
      }
      for (const tag of scan.tags) {
        for (const a of tag.attrs) {
          if (a.value === null || /^[[(*]/.test(a.name) || !USER_ATTR.test(a.name)) continue;
          for (const [rule, msg] of [...copyFindings(a.value), ...canonicalLabelFindings(a.value)]) {
            at(rule, a.index, msg);
          }
        }
      }
      for (const lit of scan.literals) {
        const userBinding = USER_ATTR.test(lit.context) || lit.context === 'interpolation';
        if (!userBinding) {
          // A literal in a non-copy binding (a class name, a route, a test id)
          // is code; only the canonical labels are worth a look there.
          for (const [rule, msg] of canonicalLabelFindings(lit.text)) at(rule, lit.index, msg);
          continue;
        }
        for (const [rule, msg] of [...copyFindings(lit.text), ...canonicalLabelFindings(lit.text)]) {
          at(rule, lit.index, msg);
        }
      }
    }
    // `| date` sits inside interpolations and bindings: read the template itself.
    const noComments = html.replace(/<!--[\s\S]*?-->/g, blankKeepLines);
    for (const m of noComments.matchAll(/\|\s*date\b(?!\w)/g)) at('raw-date', m.index, `The date pipe: ${DATE_MESSAGE}`);
    for (const f of stateFindingsTemplate(scan, relPath)) at(f.rule, f.index, f.message);
    for (const f of rawButtonFindings(scan, relPath, componentClasses)) at(f.rule, f.index, f.message);
    for (const f of primitiveFindingsTemplate(scan, relPath)) at(f.rule, f.index, f.message);
  }

  if (relPath.endsWith('.ts')) {
    const { masked, literals } = lexTs(raw);
    if (!DATE_HOMES.has(relPath)) {
      for (const f of dateFindingsTs(masked)) add('raw-date', f.index, 1, raw, `${f.what}: ${DATE_MESSAGE}`);
    }
    for (const s of inlineStylesOf(raw)) {
      for (const f of [...stateFindingsCss(s.css, relPath, hostClasses), ...primitiveFindingsCss(s.css, relPath), ...breakpointFindingsCss(s.css, relPath)]) {
        add(f.rule, f.index, s.line, s.css, f.message);
      }
    }
    for (const f of primitiveFindingsTs(masked, relPath)) add(f.rule, f.index, 1, raw, f.message);

    {
      const toasts = callRanges(masked, /\btoast\w*\s*\.\s*(?:error|success|info|warning|show)\s*\(/gi);
      const code = [
        ...callRanges(masked, /\bconsole\s*\.\s*\w+\s*\(/g),
        ...callRanges(masked, /\bnew\s+\w*Error\s*\(/g),
        ...callRanges(masked, /\b(?:import|require)\s*\(/g),
      ];
      const templateLits = new Set(templatesOf(relPath, raw).length
        ? literals.filter((l) => /\b(?:template|styles)\s*:\s*\[?\s*$/.test(masked.slice(Math.max(0, l.index - 40), l.index))).map((l) => l.index)
        : []);
      for (const lit of literals) {
        if (templateLits.has(lit.index) || inRanges(code, lit.index)) continue;
        const before = masked.slice(Math.max(0, lit.index - 60), lit.index);
        if (/\bfrom\s*$|\bimport\s*$/.test(before)) continue;
        const isCopy = inRanges(toasts, lit.index) || USER_KEY.test(before) || USER_SIGNAL.test(before);
        if (!CANONICAL_LABEL_HOMES.has(relPath)) {
          for (const [rule, msg] of canonicalLabelFindings(lit.text)) add(rule, lit.index, 1, raw, msg);
        }
        if (isCopy) {
          for (const [rule, msg] of copyFindings(lit.text)) add(rule, lit.index, 1, raw, msg);
        } else {
          // Any other string a reader might see: "Retry" as a whole string and the
          // "Failed to …" family are never right, so they need no context.
          for (const [rule, msg] of copyFindings(lit.text, { titleCase: false })) {
            if (rule === 'copy-retry' || rule === 'copy-could-not') add(rule, lit.index, 1, raw, msg);
          }
        }
      }
    }
  }
  return findings;
}

// ------------------------------------------------------------- tree scan

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) { walk(p, out); continue; }
    if (['.ts', '.html', '.css'].includes(extname(p))) out.push(p);
  }
  return out;
}

/** Run every guardrail over `root/src`. Paths in findings are relative to `root`. */
export function scanTree(root) {
  const findings = [];
  for (const p of walk(join(root, 'src'))) {
    const relPath = relative(root, p).split('\\').join('/');
    const raw = readFileSync(p, 'utf8');
    const base = p.replace(/\.(html|ts|css)$/, '');
    const read = (q) => (existsSync(q) ? readFileSync(q, 'utf8') : '');
    const siblingTs = p.endsWith('.ts') ? raw : read(`${base}.ts`);
    const componentCss = [p.endsWith('.css') ? '' : read(`${base}.css`),
      ...inlineStylesOf(siblingTs).map((s) => s.css)].join('\n');
    const componentTemplates = p.endsWith('.css')
      ? [read(`${base}.html`), ...templatesOf(`${base}.ts`, siblingTs).map((t) => t.html)]
      : [];
    findings.push(...checkFile(relPath, raw, { componentCss, componentTemplates }));
  }
  return findings;
}
