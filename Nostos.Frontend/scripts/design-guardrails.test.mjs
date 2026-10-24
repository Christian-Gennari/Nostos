/**
 * Tests for design-guardrails.mjs. Each rule gets a known-bad fixture it must
 * catch and a known-good one it must leave alone, so a rule can neither go
 * vacuous nor start crying wolf. Run by `npm run check` (`node --test`).
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkFile, titleCaseWords, lexTs, scanTemplate } from './design-guardrails.mjs';

const HTML = 'src/app/feature/feature.component.html';
const TS = 'src/app/feature/feature.component.ts';
const CSS = 'src/app/feature/feature.component.css';

const rules = (path, raw, ctx) => checkFile(path, raw, ctx).map((f) => f.rule);
const fires = (rule, path, raw, ctx) =>
  assert.ok(rules(path, raw, ctx).includes(rule), `${rule} should fire on: ${raw}`);
const quiet = (rule, path, raw, ctx) =>
  assert.ok(!rules(path, raw, ctx).includes(rule), `${rule} should NOT fire on: ${raw}\n` +
    JSON.stringify(checkFile(path, raw, ctx), null, 1));

// ------------------------------------------------------------------- dates

test('raw-date: the date pipe, DatePipe, formatDate and Intl', () => {
  fires('raw-date', HTML, '<time>{{ note.createdAt | date: "short" }}</time>');
  fires('raw-date', HTML, '<span [title]="when | date">x</span>');
  fires('raw-date', TS, "import { DatePipe } from '@angular/common';");
  fires('raw-date', TS, "const s = formatDate(d, 'd MMM', 'en');");
  fires('raw-date', TS, 'const s = new Intl.DateTimeFormat("en").format(d);');
  fires('raw-date', TS, 'const s = d.toLocaleDateString();');
  fires('raw-date', TS, 'const s = d.toLocaleTimeString([], { hour: "2-digit" });');
  fires('raw-date', TS, 'const s = new Date(x).toLocaleString();');
  fires('raw-date', TS, 'const s = book.createdAt.toLocaleString();');
});

test('raw-date: nostosDate, number formatting and the formatter itself pass', () => {
  quiet('raw-date', HTML, '<time>{{ note.createdAt | nostosDate }}</time>');
  quiet('raw-date', HTML, '<p>{{ updates | dateless }}</p>');
  quiet('raw-date', TS, "import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';");
  quiet('raw-date', TS, "const s = `${words.toLocaleString('en-US')} words`;");
  quiet('raw-date', TS, '// d.toLocaleDateString() was the old way');
  quiet('raw-date', 'src/app/core/date-formats.ts', "return formatDate(time, FORMATS[mode], DATE_LOCALE);");
});

// -------------------------------------------------------------------- copy

test('copy-retry: "Retry" as a whole label', () => {
  fires('copy-retry', HTML, '<button appButton (click)="load()">Retry</button>');
  fires('copy-retry', HTML, '<nostos-paging-status retryLabel="Retry" />');
  fires('copy-retry', TS, "const label = 'Retry';");
  quiet('copy-retry', HTML, '<button appButton (click)="load()">Try again</button>');
  quiet('copy-retry', TS, "if (copy.action === 'retry') run();");
  quiet('copy-retry', TS, "return state.jobId ? 'Retry import' : 'Try again';");
});

test('copy-ellipsis: three ASCII dots in user text', () => {
  fires('copy-ellipsis', HTML, '<input appInput placeholder="Search books..." />');
  fires('copy-ellipsis', HTML, '<button appButton>{{ busy() ? "Saving..." : "Save" }}</button>');
  fires('copy-ellipsis', HTML, '<p>Loading your notes...</p>');
  fires('copy-ellipsis', TS, "this.toast.info('Uploading...');");
  fires('copy-ellipsis', TS, "const option = { label: 'More...' };");
  quiet('copy-ellipsis', HTML, '<input appInput placeholder="Search books…" />');
  quiet('copy-ellipsis', HTML, '<app-x [items]="[...a, ...b]" />');
  quiet('copy-ellipsis', TS, 'const all = [...a, ...b];');
  quiet('copy-ellipsis', TS, "console.log('waiting...');");
});

test('copy-could-not: failures start "Could not"', () => {
  fires('copy-could-not', TS, "this.toast.error('Failed to save the note');");
  fires('copy-could-not', TS, "this.loadError.set('Unable to load audio.');");
  fires('copy-could-not', TS, 'return { message: "Couldn\'t transcribe that recording." };');
  fires('copy-could-not', HTML, '<nostos-empty-state error message="Couldn’t open this book" />');
  quiet('copy-could-not', TS, "this.toast.error('Could not save the note');");
  quiet('copy-could-not', TS, "console.error('Failed to load notes:', err);");
  quiet('copy-could-not', TS, "reject(new Error('Failed to read blob bytes.'));");
  quiet('copy-could-not', TS, '// Failed to resolve styles is the error Angular prints');
});

test('copy-title-case: two capitalised words that are not proper nouns', () => {
  fires('copy-title-case', HTML, '<button appButton>Add Book</button>');
  fires('copy-title-case', HTML, '<button appIconButton icon="x" aria-label="Close Panel"></button>');
  fires('copy-title-case', HTML, '<app-form-field label="Personal Review / Notes"></app-form-field>');
  fires('copy-title-case', TS, "this.toast.success('Book Details Saved');");
  fires('copy-title-case', TS, "const item = { label: 'Table Of Contents' };");
  quiet('copy-title-case', HTML, '<button appButton>Add book</button>');
  quiet('copy-title-case', HTML, '<button appButton>Ask Nostos</button>');
  quiet('copy-title-case', HTML, '<button appButton>Open in Brain</button>');
  quiet('copy-title-case', HTML, '<p>Import from Google Books or Open Library.</p>');
  quiet('copy-title-case', HTML, '<p>Press Enter to save. Escape cancels.</p>');
  quiet('copy-title-case', HTML, '<button appButton>Open Backups &amp; data</button>');
  quiet('copy-title-case', HTML, '<input appInput placeholder="e.g. Simone Weil" />');
  quiet('copy-title-case', HTML, '<span class="Some Class">x</span>');
});

test('titleCaseWords: sentence starts and proper names', () => {
  assert.equal(titleCaseWords('Delete Permanently'), 'Delete Permanently');
  assert.equal(titleCaseWords('Saved. Next one'), null);
  assert.equal(titleCaseWords('Works Cited'), null);
  assert.equal(titleCaseWords('Theme: Writer’s Study'), null);
  assert.equal(titleCaseWords('Export as PDF'), null);
});

// -------------------------------------------------------- canonical labels

test('reading-status-label: old labels anywhere, current ones outside their home', () => {
  fires('reading-status-label', HTML, '<span class="label">In Progress</span>');
  fires('reading-status-label', HTML, '<button appChip>Not Started</button>');
  fires('reading-status-label', TS, "const filters = ['All', 'To Read'];");
  fires('reading-status-label', HTML, '<h1 class="page-title">To read</h1>');
  fires('reading-status-label', TS, "const status = done ? 'finished' : 'not started';");
  quiet('reading-status-label', HTML, '<span class="label">{{ statusLabel.notstarted }}</span>');
  quiet('reading-status-label', HTML, '<p>An import is already in progress in another tab.</p>');
  quiet('reading-status-label', HTML, '<span>{{ busy ? "In progress" : "Waiting" }}</span>');
  quiet('reading-status-label', 'src/app/core/reading-status.ts', "export const TO_READ_LABEL = 'To read';");
});

test('unknown-author-label: one wording, one constant', () => {
  fires('unknown-author-label', HTML, "<span>{{ book.author || 'Unknown author' }}</span>");
  fires('unknown-author-label', TS, "const sub = b.author || 'Unknown author';");
  quiet('unknown-author-label', HTML, '<span>{{ book.author || unknownAuthor }}</span>');
  quiet('unknown-author-label', 'src/app/core/book-format.ts', "export const UNKNOWN_AUTHOR = 'Unknown author';");
});

// --------------------------------------------------------- hand-built states

test('hand-built-empty-state: markup and CSS that copy nostos-empty-state', () => {
  fires('hand-built-empty-state', HTML, '<div class="notes-empty-state"><p>No notes</p></div>');
  fires('hand-built-empty-state', CSS, '.empty-state { display: flex; }');
  quiet('hand-built-empty-state', HTML, '<nostos-empty-state class="books-empty-state" message="No books" />');
  quiet('hand-built-empty-state', CSS, '.books-empty-state { margin-top: 2rem; }',
    { componentTemplates: ['<nostos-empty-state class="books-empty-state" />'] });
  quiet('hand-built-empty-state', CSS, 'nostos-empty-state { flex: 1; }');
});

test('hand-built-loading: Loading… text, local spinners and skeleton keyframes', () => {
  fires('hand-built-loading', HTML, '@if (loading()) { <p class="status">Loading notes…</p> }');
  fires('hand-built-loading', HTML, '<div class="pane-spinner"></div>');
  fires('hand-built-loading', CSS, '.row-skeleton { background: var(--bg-hover); }');
  fires('hand-built-loading', CSS, '@keyframes pulse { from { opacity: .4 } }');
  fires('hand-built-loading', TS, 'styles: [`\n  @keyframes card-shimmer { to { opacity: 1 } }\n`]');
  quiet('hand-built-loading', HTML,
    '<div class="overlay"><nostos-loading-indicator size="lg" decorative />\n<span>Loading audio…</span></div>');
  quiet('hand-built-loading', HTML, '<h3>Loading and progress</h3>');
  quiet('hand-built-loading', HTML, '<nostos-loading-indicator class="palette-spinner" />');
  quiet('hand-built-loading', CSS, '@keyframes route-view-in { from { opacity: 0 } }');
  quiet('hand-built-loading', 'src/app/ui/loading-indicator/loading-indicator.component.ts',
    'styles: [`\n  @keyframes nostos-loading-spin { to { rotate: 1turn } }\n`]');
});

// -------------------------------------------------------------- raw buttons

test('raw-button: a locally styled raw button needs a primitive', () => {
  const ctx = { componentCss: '.save-action { padding: 4px; } .pill:hover { color: red; }' };
  fires('raw-button', HTML, '<button type="button" class="save-action">Save</button>', ctx);
  fires('raw-button', HTML, '<button type="button" [class.pill]="on()">On</button>', ctx);
  quiet('raw-button', HTML, '<button appButton class="save-action">Save</button>', ctx);
  quiet('raw-button', HTML, '<button appIconButton icon="x" class="save-action" aria-label="Close"></button>', ctx);
  quiet('raw-button', HTML, '<button nostosMenuItem class="save-action">Save</button>', ctx);
  quiet('raw-button', HTML, '<button type="button" class="unstyled">Save</button>', ctx);
  // A listed product control keeps its own styling.
  quiet('raw-button', 'src/app/reader/audio-reader/audio-reader.component.html',
    '<button class="play-btn">Play</button>', { componentCss: '.play-btn { width: 48px; }' });
});

// ------------------------------------------------------ other primitives

test('raw-range, raw-search-field, hand-built-cover, hand-built-pens', () => {
  fires('raw-range', HTML, '<input type="range" min="0" max="10" />');
  quiet('raw-range', HTML, '<input type="range" appRange min="0" max="10" />');

  fires('raw-search-field', HTML, '<input appInput type="search" placeholder="Search…" />');
  quiet('raw-search-field', HTML,
    '<nostos-search-field>\n  <input appInput type="search" placeholder="Search…" />\n</nostos-search-field>');

  fires('hand-built-cover', HTML, '<div class="cover-placeholder"><nostos-icon name="book" /></div>');
  fires('hand-built-cover', CSS, '.book-cover-fallback { background: var(--bg-hover); }');
  quiet('hand-built-cover', HTML, '<nostos-book-cover [src]="url" />');
  quiet('hand-built-cover', 'src/app/ui/book-cover/book-cover.component.ts',
    'template: `<span class="nostos-book-cover__placeholder"></span>`');

  fires('hand-built-pens', HTML, '<button class="pen-dot" [style.background]="fill(p)"></button>');
  fires('hand-built-pens', CSS, '.colour-swatch { width: 22px; }');
  fires('hand-built-pens', TS, "import { HIGHLIGHT_COLOURS } from '../highlight-colours';\nreadonly pens = HIGHLIGHT_COLOURS;");
  quiet('hand-built-pens', HTML, '<app-pen-swatches [value]="pen()" (chosen)="pick($event)" />');
  quiet('hand-built-pens', 'src/app/reader/epub-reader/epub-text-marks.ts', 'for (const c of HIGHLIGHT_COLOURS) {}');
});

// ------------------------------------------------------------- exceptions

test('design-allow: a written reason on or above the line', () => {
  const marked = '<!-- design-allow hand-built-loading: a one-line status in a shown result -->\n' +
    '<p class="hint">Loading formats…</p>';
  quiet('hand-built-loading', HTML, marked);
  const noReason = '<!-- design-allow hand-built-loading -->\n<p class="hint">Loading formats…</p>';
  fires('hand-built-loading', HTML, noReason);
  const otherRule = '<!-- design-allow copy-retry: wrong rule -->\n<p class="hint">Loading formats…</p>';
  fires('hand-built-loading', HTML, otherRule);
  quiet('copy-could-not', TS, "// design-allow copy-could-not: quotes the server's own words\nconst m = 'Unable to reach';");
});

test('findings carry the file and the line', () => {
  const [f] = checkFile(HTML, '<p>ok</p>\n<p>\n  <button appButton>Retry</button>\n</p>');
  assert.equal(f.rule, 'copy-retry');
  assert.equal(f.file, HTML);
  assert.equal(f.line, 3);
  const inline = 'import x;\n@Component({\n  template: `\n    <p>ok</p>\n    <button appButton>Retry</button>\n  `,\n})';
  assert.equal(checkFile(TS, inline).find((x) => x.rule === 'copy-retry').line, 5);
});

test('specs are never scanned', () => {
  assert.deepEqual(checkFile('src/app/feature/feature.component.spec.ts', "expect(x).toBe('In Progress');"), []);
});

// ------------------------------------------------------------------ lexers

test('lexTs keeps strings apart from code and comments', () => {
  const { masked, literals } = lexTs("const a = 'x // y'; // 'not a string'\nconst r = /'/g; const b = `t ${'in'} u`;");
  assert.deepEqual(literals.map((l) => l.text), ['x // y', 't  \u0001  u']);
  assert.ok(!masked.includes('not a string'));
});

test('scanTemplate reads text, attributes and expression literals', () => {
  const scan = scanTemplate('<!-- note --><p title="Hi there">Hello {{ a ? \'Yes\' : "No" }}</p>\n@if (x) { <b>Bold</b> }');
  assert.deepEqual(scan.texts.map((t) => t.text.replace(/\u0001/g, '|').trim()), ['Hello |', '|', 'Bold', '|']
    .filter((t) => t !== '|'));
  assert.deepEqual(scan.literals.map((l) => l.text), ['Yes', 'No']);
  assert.equal(scan.tags.find((t) => t.name === 'p').attrs[0].value, 'Hi there');
});
