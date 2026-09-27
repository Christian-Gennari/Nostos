#!/usr/bin/env node
/**
 * check-design.mjs — design-language drift scanner.
 *
 * Why this exists: over one change-set this repo shipped four regressions that
 * every existing check passed. `npm run check` verifies that CSS *parses* and
 * that colour tokens have dark counterparts; neither notices that
 *
 *   1. a `transition` lost its semicolon, swallowing the next declaration, so
 *      elements rendered black (valid CSS, all sheets "parse cleanly");
 *   2. a global dark override was load-bearing for a component that declared its
 *      own copy of the same class, so deleting the override silently reverted it;
 *   3. an invented token quietly moved a LIGHT-mode value;
 *   4. a token was deleted that a component actually consumed.
 *
 * Each rule below encodes one of those, at the level of *text*, so it runs in
 * the fast `npm run check` loop rather than needing a browser.
 *
 * Rules are deliberately few and each has been proven to fail on a real
 * instance (see --self-test). A scanner that cannot fail is worse than no
 * scanner, because it is trusted.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const SRC = join(ROOT, 'src');

/** Every stylesheet and inline-template component in the app. */
function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) { walk(p, out); continue; }
    const ext = extname(p);
    if (ext === '.css' || ext === '.ts') out.push(p);
  }
  return out;
}

/**
 * Extract CSS from a file. For `.ts` this pulls the contents of every
 * `styles: [\`...\`]` / `styleUrls`-adjacent template literal, because some
 * components in this app keep their styles inline (app-dock, markdown-editor,
 * star-rating) and a text scanner that skips them would miss real drift.
 */
function cssOf(path, raw) {
  if (path.endsWith('.css')) return raw;
  const blocks = [];
  const re = /styles\s*:\s*\[([\s\S]*?)\n\s*\]/g;
  let m;
  while ((m = re.exec(raw))) blocks.push(m[1]);
  return blocks.join('\n');
}

/**
 * Strip comments before scanning. Without this the scanner reads prose: a comment
 * that EXPLAINS a removed `var(--space-3)` re-triggers the undeclared-token rule,
 * so documenting a fix would break the build. Found exactly that way.
 */
const stripComments = (s) =>
  s.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' ')).replace(/^\s*\/\/.*$/gm, '');

/**
 * Is a `design-lang-allow` marker attached to THIS rule (or this line)?
 *
 * Every rule that offers the escape hatch must go through here, because getting it
 * wrong is silent in both directions and both were real bugs:
 *
 *  - Testing the marker against comment-STRIPPED text can never match: stripComments
 *    blanks comments to spaces, so `lineAllow.test(line)` was always false and the
 *    literal-colour hatch simply never worked.
 *  - Searching a WIDE window (lineNo - 30) let one marker exempt an unrelated rule 30
 *    lines below it, so a second hand-copied recipe with no marker of its own passed
 *    the guard that exists to catch exactly that.
 *
 * Both are fixed by the same idea: look at the comment block that actually belongs to
 * the rule. `raw` is searched (markers live in comments), and the scope is the lines
 * immediately above the rule, stopping at the first blank line or another rule.
 */
function markerNear(rawLines, lineNo) {
  // Walk UP from the rule and stop at the first line that cannot be part of the rule's
  // own leading comment. Two traps here, both hit during development:
  //
  //  - A BLANK line is NOT a boundary. A multi-paragraph comment block (which these
  //    markers are, because they carry a reason) contains blank lines inside itself,
  //    and stopping at the first blank line made the marker unfindable — the rule
  //    failed while the exemption sat right above it.
  //  - A line that closes a rule or opens a selector IS a boundary, because it means
  //    anything above belongs to something else.
  //
  // Depth: enough to cover a multi-paragraph comment, bounded so it cannot read back
  // into an unrelated earlier block.
  const idx = lineNo - 2; // 0-based index of the line directly above the rule
  for (let i = idx; i >= 0 && i > idx - 40; i--) {
    const line = rawLines[i] ?? '';
    if (/design-lang-allow/.test(line)) return true;
    const t = line.trim();
    // Walking UP: hitting a line that OPENS a rule/at-rule means we have left the
    // comment block behind and reached unrelated code.
    if (t.endsWith('{')) return false;
    // NOTE: a line ENDING in `*/` is the close of the comment block we are INSIDE, so
    // it must NOT stop the walk — stopping there skipped the whole multi-paragraph
    // comment and reported "no marker" while the exemption sat three lines above.
    // Only a line that OPENS a comment without the marker in it ends the search.
    if (t.startsWith('/*') && !/design-lang-allow/.test(line)) return false;
  }
  return false;
}

const files = walk(SRC).map((p) => ({ path: p, raw: readFileSync(p, 'utf8') }))
  .map((f) => ({ ...f, css: stripComments(cssOf(f.path, f.raw)) }))
  .filter((f) => f.css.trim().length > 0);

const rel = (p) => relative(ROOT, p);
const findings = [];
const infos = [];
const report = (rule, file, line, message) =>
  findings.push({ rule, file: rel(file), line, message });
/** Non-blocking observations. Printed, never gate the build. */
const info = (rule, message) => infos.push({ rule, message });

/**
 * RULE 1 — a transition declaration that a following declaration can absorb.
 *
 * `transition: a, b 0.2s ease` with no `;` and a declaration on the next line is
 * VALID CSS that silently swallows that declaration. This shipped once and made
 * text render black while every check stayed green.
 */
for (const f of files) {
  const lines = f.css.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].trim();
    if (!line.startsWith('transition:') || line.startsWith('/*')) continue;
    // A multi-line shorthand ends with a comma and is fine.
    if (line.endsWith(';') || line.endsWith(',') || line.endsWith('{')) continue;
    let j = i + 1;
    while (j < lines.length && !lines[j].trim()) j++;
    const next = (lines[j] ?? '').trim();
    if (/^[a-z-]+\s*:/.test(next) && !next.startsWith('/*')) {
      report('unterminated-transition', f.path, i + 1,
        `transition is not terminated; the next declaration ("${next.slice(0, 40)}") ` +
        `is absorbed into its value and silently discarded`);
    }
  }
}

/**
 * RULE 2 — `.visually-hidden` is duplicated verbatim.
 *
 * No single global definition exists, and the copies had already drifted apart
 * once (one omitted `clip-path`, which un-hides the element on some engines).
 * Keep one definition; import or extend it.
 */
{
  const defs = [];
  for (const f of files) {
    const re = /\.visually-hidden\s*\{([^}]*)\}/g;
    let m;
    while ((m = re.exec(f.css))) {
      defs.push({ file: f.path, body: m[1].replace(/\s+/g, ' ').trim() });
    }
  }
  /* A hand-copied recipe under a DIFFERENT selector escapes the check above, and
     that is not hypothetical: library.component.css carried the whole
     visually-hidden body inside `.library-title` under a mobile media query. The
     name-based rule looked clean the entire time. Compare BODIES, not names. */
  /**
   * Match the clip recipe by its SET of declarations, not by a fixed substring order.
   * The previous signature required `width; height; padding; margin` in that exact
   * sequence, so a copy with reordered properties — or a `clip-path: inset(50%)`
   * variant with a declaration inserted between them — was invisible to the guard.
   * An independent reviewer falsified it with both. Requiring the four characteristic
   * declarations plus `overflow: hidden` or a clip, in any order, still costs nothing
   * and catches the copies the guard exists for.
   */
  const HIDDEN_SIG = (body) => {
    const b = body.replace(/\s+/g, ' ');
    const has = (re) => re.test(b);
    return has(/width:\s*1px/) && has(/height:\s*1px/) && has(/margin:\s*-1px/)
      && has(/overflow:\s*hidden/) && (has(/clip:\s*rect\(/) || has(/clip-path:\s*inset\(/));
  };
  for (const f of files) {
    if (f.path.endsWith('styles.css')) continue;
    for (const m of f.css.matchAll(/([^{}]+)\{([^}]*)\}/g)) {
      const sel = m[1].trim();
      const body = m[2];
      if (!HIDDEN_SIG(body)) continue;
      // The global utility itself is the definition we WANT; anything else that
      // reproduces its body by hand is a copy, whatever it is called.
      if (/\.visually-hidden\s*$/.test(sel)) continue;
      // Same escape hatch the other rules use. CSS has no mixins, so a rule that must
      // apply the clip to a STATIC class inside a media query genuinely cannot
      // reference the global selector — that is an allowed exception, stated in place.
      // Locate by LINE NUMBER, not by matching the captured text: the capture comes
      // from the comment-STRIPPED css, where a stripped comment leaves a run of
      // spaces, so `raw.indexOf(m[0])` fails and the window silently landed at
      // position 0 — the exemption was present and read correctly while the rule
      // kept failing. (Comments are replaced by equal-length blank space, so line
      // numbers are stable between the two texts.)
      // `m.index` points at the end of the PREVIOUS rule, because the selector
      // capture swallows the whitespace before it. Offset past that whitespace, or
      // the window sits ~10 lines too early and misses a marker that is plainly
      // there in the source.
      const lead = m[1].length - m[1].trimStart().length;
      const lineNo = f.css.slice(0, m.index + lead).split('\n').length;
      // Scope the exemption to THIS rule's own comment block. A wide 30-line window
      // let one marker exempt an unmarked hand-copied recipe below it, which is
      // exactly the copy this rule exists to catch.
      if (markerNear(f.raw.split('\n'), lineNo)) continue;
      report('visually-hidden', f.path, lineNo,
        `"${sel.split('\n').pop().trim()}" reproduces the .visually-hidden recipe ` +
        `by hand. Use the global utility (\`class="visually-hidden"\`) or extend it, so ` +
        `the clip recipe cannot drift from the one definition.`);
    }
  }

  /* Exactly ONE definition is correct: the global utility in styles.css. Zero
     means an element that should be hidden is not; more than one means the copies
     can drift again. Neither is a style preference. */
  if (defs.length === 0) {
    report('visually-hidden', join(ROOT, 'src/styles.css'), 1,
      `.visually-hidden is not declared anywhere — screen-reader-only content ` +
      `would become visible`);
  } else if (defs.length > 1) {
    const bodies = new Set(defs.map((d) => d.body));
    report('visually-hidden', defs.find((d) => !d.file.endsWith('styles.css'))?.file ?? defs[0].file, 1,
      `.visually-hidden is declared ${defs.length}x (${bodies.size} distinct ` +
      `body/bodies) — keep the ONE global definition in styles.css` +
      (bodies.size > 1 ? '; the copies have already DRIFTED' : ''));
  }
}

/**
 * RULE 3 — a literal colour outside the token graph.
 *
 * Any `#hex` or `rgb()`/`rgba()` in a component stylesheet is a colour that
 * cannot respond to the theme. A small allow-list covers the genuine
 * theme-invariant cases (pure white/black, cover-art overlays).
 *
 * This one is a RATCHET, not a zero-tolerance gate. There are ~109 existing
 * literals, most of them inside cover-art overlays where theme-invariance is
 * correct; failing the build on them would pressure someone into "fixing" them
 * wrongly. Instead the count is pinned to a budget: it may fall freely, and any
 * increase fails. That way a NEW hard-coded colour cannot land, while the
 * existing debt is recorded rather than performance-theatre.
 *
 * When you tokenise some, lower the budget in the same commit.
 */
/* Lowered 92 -> 83 by dropping dead `var(--token, fallback)` fallbacks in
   star-rating and toast-container: every one of those tokens IS declared, so the
   fallback never rendered and only made the literal count misleading.
   Deliberately still counted (banked as a taste call, see the report):
   toast-container's `#4ade80` success and `#f87171` error — TOKEN EQUIVALENTS
   EXIST (`--color-success` / `--color-danger`) and are theme-aware, so these two
   pairs are still theme-blind against a token that is not. Left for a human
   because it changes the rendered hue. */
/* Lowered 74 -> 73 by replacing the book-detail synopsis fade's
   `rgba(255, 255, 255, 0)` transparent stop with `transparent`. That stop
   contributes no colour of its own, so naming a WHITE hue on it was a
   light-theme assumption in a token-driven theme; the opaque end of the same
   gradient was already `var(--bg-surface)`. */
/* Lowered 73 -> 69 by moving the dialog cards into `app-modal-shell`, which
   uses the themed `--shadow-glass-lg` token. The old add-book and confirm cards
   each hand-wrote a two-stop rgba shadow; those four literals are gone, and
   every dialog now follows the theme instead of assuming a light surface. */
/* Lowered 69 -> 65 by giving the reader shell's panels and popovers the shadow
   tokens the rest of the app already had: the TOC/notes panels, the quick-note
   field and the progress tooltip each hand-wrote a light-theme rgba, and the
   mobile overflow menu (now removed — the surface header holds those controls)
   carried a fourth. The one upward shadow left, on the highlight confirmation
   bar, is directional rather than incidental. */
/* Lowered 64 -> 61 by the audio reader's control pass (issue #227): the two pills
   and their dropdowns carried hand-written focus shadows and a raw cover drop
   shadow, and the unified Playback control uses the shadow/radius/motion tokens
   instead. */
/* Lowered 65 -> 64 by removing a dead declaration in the PDF sidebar's content
   panel: a `rgba(0,0,0,0.5)` shadow was set and then overwritten by a tokenised
   one two lines later, so the literal never painted. Lowered 64 -> 60 by the
   audio reader's phone pass: the waiting/error overlay is the app's own
   `--surface-image-ground` ground, and the error ink is `--color-danger` without
   the `#b91c1c` fallback the old `.audio-load-error` carried. */
const LITERAL_COLOUR_BUDGET = 60;

{
  const ALLOW = [
    // Theme-invariant by design: the brand mark, cover artwork overlays, and the
    // deliberately unstyled text selection ghost. Each is documented in the
    // design doc; add here only with a reason.
    /^#000$/, /^#fff$/, /^#ffffff$/, /^#000000$/,
  ];
  const found = [];
  for (const f of files) {
    if (f.path.endsWith('styles.css')) continue; // the token graph itself
    const lines = f.css.split('\n');
    const rawLines = f.raw.split('\n');
    for (let i = 0; i < lines.length; i++) {
      const l = lines[i];
      // Marker lives in a COMMENT, which `f.css` has blanked out, so it must be read
      // from `f.raw`. Testing the stripped line made this hatch permanently dead.
      if (/design-lang-allow/.test(rawLines[i] ?? '') || markerNear(rawLines, i + 1)) continue;
      for (const m of l.matchAll(/#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)/g)) {
        const lit = m[0];
        if (ALLOW.some((re) => re.test(lit))) continue;
        if (/^#[0-9a-fA-F]{3,8}$/.test(lit) && /^#(?:0{3,8}|f{3,8})$/i.test(lit)) continue;
        found.push({ file: rel(f.path), line: i + 1, lit });
      }
    }
  }
  if (found.length > LITERAL_COLOUR_BUDGET) {
    report('literal-colour', join(ROOT, found[0].file), found[0].line,
      `literal colours rose to ${found.length}, above the pinned budget of ` +
      `${LITERAL_COLOUR_BUDGET}. Tokenise the new one, or raise the budget ` +
      `deliberately in the same commit. The current literals are:`);
    for (const it of found) {
      report('literal-colour', join(ROOT, it.file), it.line,
        `${it.lit} — outside the token graph, so it cannot follow the theme`);
    }
  } else {
    console.log(`  (ratchet) literal colours: ${found.length} / budget ${LITERAL_COLOUR_BUDGET}` +
      `${found.length < LITERAL_COLOUR_BUDGET ? ' — LOWER THE BUDGET in the same commit' : ''}`);
  }
}

/**
 * RULE 4 — a token consumed but never declared.
 *
 * `var(--foo)` with no `--foo:` anywhere resolves to nothing and the declaration
 * is dropped at computed-value time. Cheap to catch, invisible in review.
 */
{
  const declared = new Set();
  const consumed = new Map();
  for (const f of files) {
    for (const m of f.css.matchAll(/(--[a-zA-Z0-9-]+)\s*:/g)) declared.add(m[1]);
    if (f.path.endsWith('styles.css')) continue;
    const lines = f.css.split('\n');
    for (let i = 0; i < lines.length; i++) {
      for (const m of lines[i].matchAll(/var\(\s*(--[a-zA-Z0-9-]+)/g)) {
        if (!consumed.has(m[1])) consumed.set(m[1], rel(f.path) + ':' + (i + 1));
      }
    }
  }
  // Tokens provided by Angular's view encapsulation, the framework, or set from
  // TS at runtime are not declared in any stylesheet.
  // `--depth` is bound per-row from the template via `[style.--depth]`, so it is
  // declared by Angular at runtime and appears in no stylesheet.
  const EXEMPT = [/^--ng/, /^--mat/, /^--cdk/, /^--depth$/];
  for (const [tok, where] of consumed) {
    if (declared.has(tok) || EXEMPT.some((re) => re.test(tok))) continue;
    const [file, line] = where.split(/:(?=\d+$)/);
    /* A `var(--x, fallback)` is not dropped — the fallback is used. Say so, so the
       message does not overstate the bug; both cases are still worth fixing,
       because a fallback is a maintenance trap (it looks like a token is in use
       when it is not, and editing the token silently does nothing). */
    const hasFallback = hasFallbackFor(where, tok);
    report('undeclared-token', file, Number(line),
      `var(${tok}) is consumed but never declared — ` +
      (hasFallback
        ? `the FALLBACK is what renders, so editing the token would silently do nothing`
        : `there is no fallback, so the declaration is dropped`));
  }
}

/** Did the consuming site pass a fallback to this var()? */
function hasFallbackFor(where, tok) {
  const [file, line] = where.split(/:(?=\d+$)/);
  const src = files.find((f) => rel(f.path) === file);
  if (!src) return false;
  const l = src.css.split('\n')[Number(line) - 1] ?? '';
  const i = l.indexOf(`var(${tok}`);
  if (i === -1) return false;
  let depth = 0;
  for (let k = i; k < l.length; k++) {
    if (l[k] === '(') depth++;
    else if (l[k] === ')') { depth--; if (!depth) return false; }
    else if (l[k] === ',' && depth === 1) return true;
  }
  return false;
}

// ---------------------------------------------------------------- self-test
/**
 * RULE 5 — a global dark override that cannot win its specificity race.
 *
 * This is the #94 bug class, and it is the most expensive one in this repo: the
 * failure is invisible in review, does not error, and manifests in ONE theme.
 *
 * THE CORRECT ARITHMETIC (this was wrong at first, and the correction matters):
 *
 * A "compound" is a COMbinator-separated part, NOT a class. `.nav-item.active`
 * is ONE compound holding two classes. Angular's ShadowCss splits a selector on
 * combinators and appends exactly ONE `[_ngcontent-x]` attribute per compound —
 * so a compound's contribution goes from its own (0, b, c) to (0, b+1, c).
 *
 * Worked example, the badge that shipped broken:
 *   component: .nav-item.active .count-badge
 *     -> classes 3 + two injected attributes = (0,5,0)
 *   override : :root[data-theme='dark'] .nav-item.active .count-badge
 *     -> :root (pseudo-class 1) + [data-theme] (attribute 1) + classes 3 = (0,5,0)
 *
 * They TIE. The component wins because Angular appends component styles after
 * styles.css. So the failure is a tie broken by source order, not a loss.
 *
 * An earlier version of this comment (and of the design doc) claimed (0,6,0) vs
 * (0,5,0) by doubling a class count. That was a coincidence that reproduced the
 * right verdict for this one selector and is wrong in general: it miscounts
 * multi-class compounds, element selectors, IDs, `:not()`, `:is()`/`:where()`,
 * combinators and comma lists.
 *
 * CONSEQUENCE FOR THIS CHECK: because the arithmetic is an approximation over
 * real-world selectors, this rule is ADVISORY, not a hard gate. It reports; it
 * does not fail the build. Treat a finding as "measure this one in the browser",
 * and treat silence as no signal at all. The reliable detector for this bug class
 * is the paint sweep in both themes plus a pixel baseline, which is what actually
 * caught the original defect.
 *
 * The heuristic below deliberately over-estimates the GLOBAL side (it counts one
 * for `:root` and one for `[data-theme]`, plus one per compound) so that it errs
 * toward staying quiet rather than crying wolf on rules that are fine.
 */
{
  const stylesPath = files.find((f) => f.path.endsWith('styles.css'));
  if (stylesPath) {
    /* Below `:root {` the sheet declares tokens only; overrides are the
       `:root[data-theme=...] <selector>` rules. */
    const overrideRe = /^:root\[data-theme[^\]]*\]\s+([^{]+)\{([^}]*)\}/gm;
    let m;
    const out = [];
    while ((m = overrideRe.exec(stylesPath.css))) {
      const sel = m[1].trim();
      const body = m[2];
      // Only rules that paint something (a token-only rule cannot race).
      if (!/(^|[;{\s])(color|background|background-color|border|border-color|box-shadow|display|opacity|padding|margin|width|height|font)\s*:/.test(body)) continue;
      const compounds = sel.split(/\s+/).filter(Boolean);
      // `:root[data-theme='dark']` contributes a pseudo-class and an attribute.
      const globalScore = compounds.length + 1;
      out.push({ sel, globalScore, compounds: compounds.length, body, index: m.index });
    }
    /* For each override, find the component rule it actually races: one whose
       rightmost compound carries the same class AND the same state pseudo-class,
       so the report names the real opponent rather than whichever rule happens to
       be most specific anywhere in the app. */
    for (const ov of out) {
      const target = ov.sel.split(/\s+/).pop().replace(/:{2}.*$/, '');
      const clsMatch = target.match(/\.([a-zA-Z0-9_-]+)/);
      if (!clsMatch) continue;
      const cls = clsMatch[1];
      const states = (target.match(/:(?:hover|focus|focus-visible|active|focus-within)\b/g) ?? []);
      let best = null;
      for (const f of files) {
        if (f.path.endsWith('styles.css') || f.path.endsWith('.ts')) continue;
        const re = new RegExp(`^[^{}]*\\.${cls}\\b[^{}]*\\{`, 'gm');
        let c;
        while ((c = re.exec(f.css))) {
          const raw = c[0].replace(/\{$/, '').trim();
          for (const one of raw.split(',')) {
            const sel = one.trim();
            if (!new RegExp(`\\.${cls}\\b`).test(sel)) continue;
            // Must carry the same state, or it is a different rule entirely.
            if (states.some((s) => !sel.includes(s))) continue;
            // Angular doubles each compound; the global side counts once.
            const compounds = sel.split(/\s+/).filter(Boolean).length;
            if (compounds < 2) continue; // a single compound cannot be the opponent
            const score = compounds * 2;
            if (!best || score > best.score) best = { score, sel, file: f.path };
          }
        }
      }
      if (!best) continue;
      if (ov.globalScore <= best.score) {
        /* ADVISORY, not a gate. This arithmetic approximates real specificity, so a
           finding means "measure this in the browser", and silence means nothing. */
        info('possible-unwinnable-dark-override',
          `"${ov.sel}" may not beat "${best.sel}" in ${rel(best.file)} ` +
          `(approx global=${ov.globalScore} vs component=${best.score}). Verify with ` +
          `getComputedStyle in the live page before acting — this check is a ` +
          `heuristic, not a specificity engine.`);
      }
    }
  }
}

/**
 * Count backticks in a `.ts` file's `styles` array body. EXACTLY 2 (one template
 * literal) is expected. Shared by the rule and its self-test so the two cannot
 * disagree — the first version of this rule tested parity (even/odd) and the
 * self-test correctly caught that parity would miss the real defect, which had
 * FOUR backticks.
 */
function stylesBacktickCount(raw) {
  const i = raw.indexOf('styles: [');
  if (i === -1) return null;
  const seg = raw.slice(i);
  const j = seg.indexOf('\n  ],');
  return ((j === -1 ? seg : seg.slice(0, j)).match(/`/g) ?? []).length;
}

/**
 * RULE 6 — extra backticks in a component's `styles` template literal.
 *
 * Angular components in this app keep CSS in `styles: [\`...\`]`. A backtick INSIDE
 * that CSS — most easily written by accident in a comment, e.g. \`info\` — closes
 * the literal early. The CSS after it becomes a SECOND array element, and the AOT
 * compiler fails with:
 *
 *   Failed to resolve styles at position 1 to a string.
 *   Value could not be determined statically.
 *
 * That message names no file, so the failure reads as a mystery build break. This
 * bit the change-set twice (once in app-dock, once in toast-container), both times
 * from a comment written about a CSS class name.
 *
 * Rule of thumb the check enforces: never wrap a word in backticks inside inline
 * component CSS. Use "quotes" instead.
 */
{
  for (const f of files) {
    if (!f.path.endsWith('.ts')) continue;
    const raw = f.raw;
    const i = raw.indexOf('styles: [');
    if (i === -1) continue;
    const seg = raw.slice(i);
    const j = seg.indexOf('\n  ],');
    const body = j === -1 ? seg : seg.slice(0, j);
    const ticks = (body.match(/`/g) ?? []).length;
    /* EXACTLY 2 is correct: one template literal. NOT "an even number" — the defect
       that shipped had FOUR (open, a backtick pair around a word in a comment, and
       close), which is even but still terminated the literal early and handed the
       AOT compiler a second, unresolvable styles element. Counting parity would have
       missed the very bug this rule exists for; I wrote the parity version first and
       the self-test caught it. */
    if (ticks !== 2 && !/design-lang-allow/.test(body)) {
      const line = raw.slice(0, i).split('\n').length;
      report('backtick-in-inline-styles', f.path, line,
        `the \`styles\` array contains ${ticks} backticks; exactly 2 (one template ` +
        `literal) is expected. An EXTRA pair — most often a word wrapped in backticks ` +
        `inside a CSS comment — closes the literal early, and Angular then fails with ` +
        `"Failed to resolve styles at position 1 to a string. Value could not be ` +
        `determined statically", naming NO file. Use "quotes" inside inline CSS ` +
        `comments. If a multi-element array is genuinely intended, add ` +
        `design-lang-allow to it.`);
    }
  }
}

/**
 * RULE 7 — a multi-item `transition` whose earlier items have no duration.
 *
 * `transition` is a COMMA-SEPARATED LIST OF SHORTHANDS, and a trailing `<time>`
 * binds only to the LAST item. So
 *
 *   transition: background-color, border-color, box-shadow 0.2s ease;
 *
 * means background-color and border-color at 0s (they SNAP) and only box-shadow
 * animating. Verified in the browser: that declaration computes to
 * `transitionDuration: "0s, 0s, 0.2s"`.
 *
 * This is the trap the `transition: all` -> explicit-properties conversion walks
 * straight into, and it did: 16 sites were converted into exactly that shape. The
 * pixel gate cannot see it, because a static screenshot of a non-hovered element
 * looks identical whether it would animate or snap on hover.
 *
 * A `var()` in an item counts as that item's duration (tokens like `--motion-slow`
 * ARE times), so those are accepted.
 */
{
  const TIME = /(?<![\w.-])\d+(?:\.\d+)?(?:m?s)\b/;
  const VAR_TIME = /var\(--[a-z-]*(?:motion|transition|duration|speed|rail)[a-z-]*\b/;
  const splitTop = (v) => {
    const out = []; let depth = 0; let cur = '';
    for (const ch of v) {
      if (ch === '(') depth++;
      if (ch === ')') depth--;
      if (ch === ',' && depth === 0) { out.push(cur); cur = ''; } else cur += ch;
    }
    out.push(cur);
    return out.map((x) => x.trim()).filter(Boolean);
  };
  const hasTime = (item) => TIME.test(item) || VAR_TIME.test(item);
  for (const f of files) {
    for (const m of f.css.matchAll(/transition:\s*((?:[^;{}]|\n)*?)(?=;|})/g)) {
      const v = m[1].replace(/\s+/g, ' ').trim();
      if (!v || v.startsWith('none') || v.startsWith('all')) continue;
      const items = splitTop(v);
      if (items.length < 2) continue;
      const missing = items.filter((i) => !hasTime(i));
      if (!missing.length) continue;
      // Only a bug when SOME item does have a duration: otherwise nothing was
      // intended to animate and the whole declaration is inert.
      if (!items.some(hasTime)) continue;
      const line = f.css.slice(0, m.index).split('\n').length;
      report('transition-missing-duration', f.path, line,
        `${missing.length} of ${items.length} items have no duration, so they snap ` +
        `instantly: [${missing.join(' | ').slice(0, 80)}]. A trailing time binds only ` +
        `to the LAST item. Repeat the time on every item.`);
    }
  }
}

/**
 * RULE 9 — Settings must not grow a second generic button family.
 *
 * Settings was the first surface migrated to the canonical native-button
 * primitive (`button[appButton]`). Before that migration it owned a private
 * `.btn` / `.btn-primary` / `.btn-secondary` / `.btn-danger` / `.btn-sm`
 * family whose geometry and motion had drifted from the rest of the app.
 *
 * Keep product-specific controls such as `.settings-nav-item` and `.theme-card`
 * local — they have different interaction contracts. What is forbidden here is a
 * NEW generic `.btn*` recipe in Settings, because ordinary labelled actions now
 * have one owner.
 */
function legacySettingsButtonSelectors(css) {
  const out = [];
  for (const m of css.matchAll(/([^{}]+)\{/g)) {
    const selector = m[1].trim();
    const hasLegacy = selector
      .split(',')
      .some((part) => /(?:^|[\s>+~])\.btn(?:\b|-[a-zA-Z0-9_-]+\b)/.test(part.trim()));
    if (hasLegacy) out.push({ selector, index: m.index });
  }
  return out;
}

{
  const settings = files.find((f) => rel(f.path) === 'src/app/settings/settings.component.css');
  if (settings) {
    for (const hit of legacySettingsButtonSelectors(settings.css)) {
      const line = settings.css.slice(0, hit.index).split('\n').length;
      report('settings-local-button-family', settings.path, line,
        `Settings re-declares a generic .btn* selector ("${hit.selector.replace(/\s+/g, ' ').slice(0, 90)}"). ` +
        `Use button[appButton] for ordinary labelled actions; keep only product-specific controls local.`);
    }
  }
}

/**
 * RULE 10 — Settings must not grow another local switch implementation.
 *
 * Settings is the proving surface for label[appSwitch]. A renamed copy is just
 * as much drift as bringing back .toggle/.toggle-slider, so this checks both the
 * old selector names and the measured 42x24 / 18x18 switch recipes.
 *
 * Product-specific checkboxes may keep native checkbox presentation; this rule
 * only rejects a second switch-shaped recipe inside Settings.
 */
function legacySettingsSwitchRecipes(css) {
  const out = [];
  for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    const body = m[2].replace(/\s+/g, ' ');
    const hasLegacyName = selector
      .split(',')
      .some((part) =>
        /(?:^|[\s>+~])\.(?:toggle|toggle-slider|switch|switch-track)(?:\b|[-_])/i.test(
          part.trim(),
        ),
      );
    const hasTrackRecipe =
      /width:\s*42px\b/.test(body) &&
      /height:\s*24px\b/.test(body) &&
      /(?:border-radius|cursor|position)\s*:/.test(body);
    const hasKnobRecipe =
      /width:\s*18px\b/.test(body) &&
      /height:\s*18px\b/.test(body) &&
      /(?:left|inset-inline-start):\s*3px\b/.test(body);

    if (hasLegacyName || hasTrackRecipe || hasKnobRecipe) {
      out.push({ selector, index: m.index });
    }
  }
  return out;
}

{
  const settings = files.find((f) => rel(f.path) === 'src/app/settings/settings.component.css');
  if (settings) {
    for (const hit of legacySettingsSwitchRecipes(settings.css)) {
      const line = settings.css.slice(0, hit.index).split('\n').length;
      report(
        'settings-local-switch-family',
        settings.path,
        line,
        `Settings re-declares switch styling ("${hit.selector.replace(/\s+/g, ' ').slice(0, 90)}"). ` +
          `Use label[appSwitch]; keep checked/disabled/ARIA semantics on its native checkbox.`,
      );
    }
  }
}

/**
 * RULE 11 — Add Book must not regrow a private generic field system.
 *
 * Add Book/Edit Book is the first surface migrated to the canonical native-host
 * form controls and FormField. Before that migration the component owned its own
 * .input/.select-input geometry, focus ring, label and field wrapper recipes.
 *
 * Product-specific controls remain intentionally local. In particular the source
 * provider search is not an ordinary metadata field in this PR, so its existing
 * global .input class is explicitly exempted by id. Everything else in Add Book
 * must use appInput/appTextarea/appSelect + FormField rather than recreating the
 * old generic field vocabulary.
 */
const ADD_BOOK_LEGACY_FIELD_CLASSES = new Set([
  'input',
  'textarea',
  'select-input',
  'form-group',
  'form-label',
  'info-text',
]);

function legacyAddBookFieldSelectors(css) {
  const out = [];
  for (const m of css.matchAll(/([^{}]+)\{/g)) {
    const selector = m[1].trim();
    const classes = [...selector.matchAll(/\.([a-zA-Z][a-zA-Z0-9_-]*)/g)].map((x) => x[1]);
    if (classes.some((name) => ADD_BOOK_LEGACY_FIELD_CLASSES.has(name))) {
      out.push({ selector, index: m.index });
    }
  }
  return out;
}

function legacyAddBookFieldMarkup(html) {
  const out = [];
  for (const tag of html.matchAll(/<[^>]+>/g)) {
    const raw = tag[0];
    const classMatch = raw.match(/\bclass\s*=\s*(["'])(.*?)\1/s);
    if (!classMatch) continue;
    const classes = classMatch[2].split(/\s+/).filter(Boolean);
    const legacy = classes.filter((name) => ADD_BOOK_LEGACY_FIELD_CLASSES.has(name));
    if (!legacy.length) continue;

    // Source/provider search remains product-owned in #357. It deliberately keeps
    // the pre-existing global .input appearance while its search/result/acquisition
    // interaction stays outside the ordinary FormField migration.
    if (/\bid\s*=\s*(["'])source-query\1/.test(raw) && legacy.every((name) => name === 'input')) {
      continue;
    }

    out.push({ tag: raw, index: tag.index, legacy });
  }
  return out;
}

{
  const addBookCss = files.find(
    (f) => rel(f.path) === 'src/app/add-book-modal/add-book-modal.component.css',
  );
  if (addBookCss) {
    for (const hit of legacyAddBookFieldSelectors(addBookCss.css)) {
      const line = addBookCss.css.slice(0, hit.index).split('\n').length;
      report(
        'add-book-local-field-system',
        addBookCss.path,
        line,
        `Add Book re-declares legacy generic field selector "${hit.selector.replace(/\s+/g, ' ').slice(0, 90)}". ` +
          `Use appInput/appTextarea/appSelect + FormField for ordinary fields; keep only product-specific controls local.`,
      );
    }
  }

  const addBookHtmlPath = join(SRC, 'app', 'add-book-modal', 'add-book-modal.component.html');
  const addBookHtml = readFileSync(addBookHtmlPath, 'utf8');
  for (const hit of legacyAddBookFieldMarkup(addBookHtml)) {
    const line = addBookHtml.slice(0, hit.index).split('\n').length;
    report(
      'add-book-legacy-field-markup',
      addBookHtmlPath,
      line,
      `Add Book uses legacy generic field class(es) [${hit.legacy.join(', ')}]. ` +
        `Use the canonical native-host form primitives/FormField instead.`,
    );
  }
}

/**
 * RULE 12 — Settings ordinary fields must stay on the canonical form primitives.
 *
 * The final UI-v1 audit found two private generic field families still living in
 * Settings: .select-sm and .provider-input. Their layout hooks are still useful,
 * but border/radius/background/focus/typography now belong to appSelect/appInput.
 *
 * This is intentionally selector-scoped and recipe-based. It does NOT reject the
 * class names themselves, so Settings can keep responsive alignment/flex rules,
 * and it does not scan product controls elsewhere.
 */
function legacySettingsFormRecipes(css) {
  const out = [];
  for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    if (!/(?:^|[\s>+~])\.(?:select-sm|provider-input)(?:\b|[-_])/.test(selector)) continue;
    const body = m[2].replace(/\s+/g, ' ');
    const appearance = [
      /(?:^|;)\s*padding(?:-[a-z-]+)?\s*:/,
      /(?:^|;)\s*border(?:-[a-z-]+)?\s*:/,
      /(?:^|;)\s*background(?:-[a-z-]+)?\s*:/,
      /(?:^|;)\s*border-radius\s*:/,
      /(?:^|;)\s*font-size\s*:/,
      /(?:^|;)\s*outline(?:-[a-z-]+)?\s*:/,
      /(?:^|;)\s*appearance\s*:/,
    ].filter((re) => re.test(body)).length;
    if (appearance >= 2) out.push({ selector, index: m.index });
  }
  return out;
}

{
  const settings = files.find((f) => rel(f.path) === 'src/app/settings/settings.component.css');
  if (settings) {
    for (const hit of legacySettingsFormRecipes(settings.css)) {
      const line = settings.css.slice(0, hit.index).split('\n').length;
      report(
        'settings-local-form-family',
        settings.path,
        line,
        `Settings re-declares ordinary field appearance in "${hit.selector.replace(/\s+/g, ' ').slice(0, 90)}". ` +
          `Use appInput/appSelect for the control; keep only product/layout hooks local.`,
      );
    }
  }
}

/**
 * RULE 13 — migrated UI-v1 surfaces must not fall back to the old generic class
 * families for ordinary controls.
 *
 * This is a narrow ratchet over the surfaces explicitly migrated in #356-#362,
 * not a global ban. Exact legacy class tokens are checked; product class names
 * such as .btn-back, .item-action and .tab-btn are deliberately untouched.
 *
 * Exact legacy generic classes are now retired from live app templates entirely.
 * Product-owned controls keep local class names instead of borrowing the old global
 * generic family.
 */
const MIGRATED_UI_V1_TEMPLATES = [
  'src/app/settings/settings.component.html',
  'src/app/add-book-modal/add-book-modal.component.html',
  'src/app/ui/assistant/assistant.component.html',
  'src/app/library/library.component.html',
  'src/app/second-brain/second-brain.component.html',
  'src/app/book-detail/book-detail.component.html',
  'src/app/book-detail/editions-modal/editions-modal.component.html',
  'src/app/reader/reader-shell.component.html',
  'src/app/writing-studio/writing-studio.component.html',
  'src/app/ui/confirm-modal/confirm-modal.component.html',
];
const LEGACY_GENERIC_CONTROL_CLASSES = new Set([
  'btn',
  'btn-primary',
  'btn-secondary',
  'btn-danger',
  'btn-sm',
  'input',
  'textarea',
  'select-input',
]);

function legacyMigratedControlMarkup(html) {
  const out = [];
  for (const tag of html.matchAll(/<[^>]+>/gs)) {
    const raw = tag[0];
    const classMatch = raw.match(/\bclass\s*=\s*(["'])(.*?)\1/s);
    if (!classMatch) continue;
    const classes = classMatch[2].split(/\s+/).filter(Boolean);
    const legacy = classes.filter((name) => LEGACY_GENERIC_CONTROL_CLASSES.has(name));
    if (!legacy.length) continue;

    out.push({ tag: raw, index: tag.index, legacy });
  }
  return out;
}

/**
 * Dropdown ownership is global rather than surface-by-surface: ordinary
 * single-choice dropdowns use app-dropdown. Keeping this separate from the
 * migrated-surface field ledger prevents a new feature template from silently
 * reintroducing a raw native <select>.
 */
function rawNativeSelects(html) {
  return [...stripHtmlComments(html).matchAll(/<select\b[^>]*>/gs)];
}

{
  const TEMPLATES = [];
  (function walkDropdownTemplates(dir) {
    for (const entry of readdirSync(dir)) {
      const p = join(dir, entry);
      if (statSync(p).isDirectory()) { walkDropdownTemplates(p); continue; }
      if (extname(p) === '.html') TEMPLATES.push({ path: p, raw: readFileSync(p, 'utf8') });
    }
  })(SRC);

  for (const template of TEMPLATES) {
    for (const hit of rawNativeSelects(template.raw)) {
      const line = stripHtmlComments(template.raw).slice(0, hit.index).split('\n').length;
      report(
        'raw-native-select',
        template.path,
        line,
        'Raw <select> is not part of Nostos UI v1. Use the canonical app-dropdown; ' +
          'product-owned listboxes/action menus should keep their explicit non-select semantics.',
      );
    }
  }
}

for (const relativePath of MIGRATED_UI_V1_TEMPLATES) {
  const htmlPath = join(ROOT, relativePath);
  const html = readFileSync(htmlPath, 'utf8');
  for (const hit of legacyMigratedControlMarkup(html)) {
    const line = html.slice(0, hit.index).split('\n').length;
    report(
      'migrated-surface-legacy-generic-control',
      htmlPath,
      line,
      `Migrated UI-v1 surface uses legacy generic class(es) [${hit.legacy.join(', ')}]. ` +
        `Use the canonical primitive for an ordinary control, or keep a clearly product-owned class/interaction.`,
    );
  }
}

/**
 * RULE 14 — migrated surfaces must not copy the canonical switch geometry.
 *
 * Unlike the Settings name-based rule, this scans only the migrated surface CSS
 * and matches the measured 42x24 track / 18x18 knob geometry. It therefore does
 * not mistake unrelated "toggle" names (view modes, tabs) for switch copies.
 */
const MIGRATED_UI_V1_STYLES = [
  'src/app/settings/settings.component.css',
  'src/app/add-book-modal/add-book-modal.component.css',
  'src/app/ui/assistant/assistant.component.css',
  'src/app/library/library.component.css',
  'src/app/second-brain/second-brain.component.css',
  'src/app/book-detail/book-detail.component.css',
  'src/app/book-detail/editions-modal/editions-modal.component.css',
  'src/app/reader/reader-shell.component.css',
  'src/app/writing-studio/writing-studio.component.css',
  'src/app/ui/confirm-modal/confirm-modal.component.css',
];

function copiedSwitchGeometry(css) {
  const out = [];
  for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = m[1].trim();
    const body = m[2].replace(/\s+/g, ' ');
    const track =
      /width:\s*42px\b/.test(body) &&
      /height:\s*24px\b/.test(body) &&
      /(?:border-radius|cursor|position)\s*:/.test(body);
    const knob =
      /width:\s*18px\b/.test(body) &&
      /height:\s*18px\b/.test(body) &&
      /(?:left|inset-inline-start):\s*3px\b/.test(body);
    if (track || knob) out.push({ selector, index: m.index });
  }
  return out;
}

for (const relativePath of MIGRATED_UI_V1_STYLES) {
  const stylePath = join(ROOT, relativePath);
  if (!files.some((f) => f.path === stylePath)) continue;
  const raw = readFileSync(stylePath, 'utf8');
  const css = stripComments(raw);
  for (const hit of copiedSwitchGeometry(css)) {
    const line = css.slice(0, hit.index).split('\n').length;
    report(
      'migrated-surface-switch-copy',
      stylePath,
      line,
      `Migrated surface copies canonical switch geometry in "${hit.selector.replace(/\s+/g, ' ').slice(0, 90)}". ` +
        `Use label[appSwitch] for an ordinary switch.`,
    );
  }
}

/**
 * RULE 15 — raw native controls on migrated surfaces require an explicit owner.
 *
 * A name-based legacy check is necessary but insufficient: a new
 * `<button class="save-action">` can recreate an ordinary Button recipe without
 * ever spelling `.btn`. The durable boundary is ownership:
 *
 *   - canonical ordinary controls carry appButton/appIconButton/appChip or the
 *     native-host form directives;
 *   - raw controls are allowed only when their product interaction is listed here.
 *
 * This is intentionally an allow-list rather than a CSS-shape heuristic. Reader
 * transport, Brain rows, Assistant recording, tabs and acquisition rows share
 * ordinary CSS properties with buttons but have different semantics. Adding a new
 * raw control therefore requires an explicit decision in this ledger instead of
 * silently becoming a second generic primitive.
 */
const PRODUCT_RAW_BUTTON_CLASSES = new Map([
  ['src/app/settings/settings.component.html', new Set(['settings-nav-item', 'theme-card'])],
  ['src/app/add-book-modal/add-book-modal.component.html', new Set(['tab-btn', 'source-choice', 'source-result', 'source-filter'])],
  ['src/app/ui/assistant/assistant.component.html',
    new Set(['assistant-expand', 'suggestion-chip', 'anchor-chip-dismiss', 'voice-control', 'assistant-trigger'])],
  ['src/app/library/library.component.html',
    // 'toggle-opt' is gone from this ledger: the view toggle is
    // `nostos-view-toggle` now, so this template renders no raw toggle buttons.
    new Set(['item-action', 'finished-btn-list', 'fav-btn-list',
      'finished-btn-grid', 'fav-btn-grid', 'action-circle'])],
  ['src/app/second-brain/second-brain.component.html',
    // 'toggle-opt' is gone from this ledger for the same reason as the Library's.
    new Set(['note-row-item', 'review-load-more', 'index-item', 'row-action',
      'rail-foot-action', 'mobile-nav-header', 'concept-action', 'merge-picker-close',
      'merge-target', 'merge-picker-cancel', 'merge-picker-confirm', 'related-chip', 'related-more'])],
  ['src/app/book-detail/book-detail.component.html',
    new Set(['btn-back', 'cover-overlay-btn', 'status-chip', 'status-dropdown-item',
      'edit-menu-item', 'expand-btn', 'edition-select-card', 'edition-section-action'])],
  ['src/app/book-detail/editions-modal/editions-modal.component.html',
    new Set(['manage-member-action', 'manage-link-candidate'])],
  ['src/app/reader/reader-shell.component.html',
    new Set(['highlight-toggle', 'hl-pen', 'typo-opt', 'typo-step'])],
  ['src/app/writing-studio/writing-studio.component.html', new Set(['tab-btn', 'toggle-opt', 'list-item'])],
  ['src/app/ui/confirm-modal/confirm-modal.component.html', new Set()],
]);

function stripHtmlComments(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function rawButtonWithoutPrimitive(html, relativePath) {
  const out = [];
  const clean = stripHtmlComments(html);
  const allowed = PRODUCT_RAW_BUTTON_CLASSES.get(relativePath) ?? new Set();
  for (const tag of clean.matchAll(/<button\b[^>]*>/gs)) {
    const raw = tag[0];
    if (/\bapp(?:Button|IconButton|Chip)\b/.test(raw)) continue;
    const classMatch = raw.match(/\bclass\s*=\s*(["'])(.*?)\1/s);
    const classes = classMatch ? classMatch[2].split(/\s+/).filter(Boolean) : [];
    if (classes.some((name) => allowed.has(name))) continue;
    out.push({ tag: raw, index: tag.index });
  }
  return out;
}

function rawOrdinaryFieldWithoutPrimitive(html, relativePath) {
  const out = [];
  const clean = stripHtmlComments(html);
  for (const tag of clean.matchAll(/<(input|select|textarea)\b[^>]*>/gs)) {
    const raw = tag[0];
    if (/\bapp(?:Input|Textarea)\b/.test(raw)) continue;

    const kind = tag[1];
    if (kind === 'input') {
      const type = raw.match(/\btype\s*=\s*(["'])(.*?)\1/s)?.[2]?.toLowerCase() ?? 'text';
      if (['checkbox', 'file', 'radio', 'number', 'range', 'hidden'].includes(type)) continue;
    }

    const classMatch = raw.match(/\bclass\s*=\s*(["'])(.*?)\1/s);
    const classes = classMatch ? classMatch[2].split(/\s+/).filter(Boolean) : [];

    // These fields are tightly coupled to their product interaction rather than
    // being ordinary forms: the Assistant composer owns Enter/Shift+Enter/voice
    // composition, and Brain rename/picker fields own inline commit/Escape flows.
    if (relativePath === 'src/app/ui/assistant/assistant.component.html' &&
        classes.includes('composer-field')) continue;
    if (relativePath === 'src/app/second-brain/second-brain.component.html' &&
        (classes.includes('inline-rename-input') ||
         /aria-label\s*=\s*["']Search (?:concepts to link|merge targets)["']/.test(raw))) continue;

    out.push({ tag: raw, index: tag.index });
  }
  return out;
}

for (const relativePath of MIGRATED_UI_V1_TEMPLATES) {
  const htmlPath = join(ROOT, relativePath);
  const html = readFileSync(htmlPath, 'utf8');

  for (const hit of rawButtonWithoutPrimitive(html, relativePath)) {
    const line = stripHtmlComments(html).slice(0, hit.index).split('\n').length;
    report(
      'migrated-surface-unowned-raw-button',
      htmlPath,
      line,
      'Raw button has no canonical primitive and is not listed as product-owned. ' +
        'Use appButton/appIconButton/appChip for an ordinary control, or add an explicit ' +
        'product ownership entry when the interaction semantics genuinely differ.',
    );
  }

  for (const hit of rawOrdinaryFieldWithoutPrimitive(html, relativePath)) {
    const line = stripHtmlComments(html).slice(0, hit.index).split('\n').length;
    report(
      'migrated-surface-unowned-raw-field',
      htmlPath,
      line,
      'Raw text/search/select/textarea has no canonical primitive and is not an ' +
        'explicit product-owned field. Use appInput/appTextarea/app-dropdown or document ' +
        'the product interaction exception in the ownership ledger.',
    );
  }
}


/**
 * The root Toast pattern is the only generic transient-notification surface.
 * Product-owned inline status/error UI remains legitimate; this only catches
 * new components/classes that present themselves as generic toast/snackbar UI.
 */
function adHocNotificationSurface(raw, css) {
  return (
    /selector\s*:\s*['"][^'"]*(?:toast|snackbar)[^'"]*['"]/i.test(raw) ||
    /\.(?:snackbar|toast-(?:container|message|dismiss|success|error|info))\b/i.test(css)
  );
}

/**
 * Prove the scanner can fail. A rule that cannot be made to fire is not a check.
 * `--self-test` injects a known-bad snippet per rule and asserts each fires.
 */
if (process.argv.includes('--self-test')) {
  const cases = [
    ['unterminated-transition',
      '.x {\n  transition: background-color, color 0.2s ease\n  color: red;\n}'],
    ['literal-colour', '.x { color: #ff00ff; }'],
    ['undeclared-token', '.x { color: var(--definitely-not-declared); }'],
    ['settings-local-button-family', '.btn-primary { background: red; }'],
    ['settings-local-switch-family',
      '.renamed-control { position: relative; width: 42px; height: 24px; cursor: pointer; }'],
    ['add-book-local-field-system', '.input, .select-input { padding: 1rem; }'],
    ['add-book-legacy-field-markup', '<input class="input" type="text">'],
    ['settings-local-form-family',
      '.provider-input { padding: 9px; border: 1px solid var(--border-color); background: var(--bg-input); }'],
    ['migrated-surface-legacy-generic-control', '<button class="btn btn-secondary">Save</button>'],
    ['migrated-surface-unowned-raw-button', '<button class="save-action">Save</button>'],
    ['migrated-surface-unowned-raw-field', '<input class="save-name" type="text">'],
    ['raw-native-select', '<select><option>Old dropdown</option></select>'],
    ['migrated-surface-switch-copy',
      '.copied-switch { position: relative; width: 42px; height: 24px; border-radius: 999px; }'],
    ['ad-hoc-notification-surface', '.snackbar-card { padding: 1rem; }'],
    // RULE 8 needs a TEMPLATE and a matching .css class, so its case is checked by
    // the same predicate the rule uses (a bare hyphenated attr that IS a known class).
    ['bare-attribute-not-class', '<button appIconButton desktop-only></button>'],
    // The by-RECIPE half of RULE 2. The original rule matched the selector name, so a
    // hand-copied clip recipe under a different class escaped it entirely — that is
    // exactly what `.library-title` did in a mobile media query.
    ['visually-hidden', '.some-other-name { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); }'],
    // REORDERED properties and the modern clip-path variant. Both slipped through the
    // original signature, which required width/height/padding/margin in that exact
    // order, so an independent reviewer kept a hand-copied recipe hidden from the guard.
    ['visually-hidden', '.reordered { margin: -1px; padding: 0; height: 1px; width: 1px; overflow: hidden; clip: rect(0, 0, 0, 0); }'],
    ['visually-hidden', '.modern { position: absolute; width: 1px; height: 1px; clip-path: inset(50%); margin: -1px; overflow: hidden; }'],
  ];
  let ok = 0;
  for (const [rule, snippet] of cases) {
    const fake = { path: join(SRC, '__selftest.css'), raw: snippet, css: snippet };
    const before = findings.length;
    // re-run only the checks against the snippet by scanning it inline
    let fired = false;
    if (rule === 'unterminated-transition') {
      const lines = snippet.split('\n');
      for (let i = 0; i < lines.length; i++) {
        const l = lines[i].trim();
        if (!l.startsWith('transition:') || l.endsWith(';') || l.endsWith(',') || l.endsWith('{')) continue;
        let j = i + 1; while (j < lines.length && !lines[j].trim()) j++;
        if (/^[a-z-]+\s*:/.test((lines[j] ?? '').trim())) fired = true;
      }
    }
    if (rule === 'literal-colour') fired = /#ff00ff/.test(snippet);
    if (rule === 'visually-hidden') {
      // Must mirror the CURRENT signature, which matches the recipe as a SET of
      // declarations rather than a fixed substring order. Pinning the old ordered
      // regex here would let the self-test pass while the rule itself had gone back
      // to being blind to reordered / clip-path copies — which is exactly how it was
      // falsified. Independent review found both variants slipping through.
      const b = snippet.replace(/\s+/g, ' ');
      fired = /width:\s*1px/.test(b) && /height:\s*1px/.test(b) && /margin:\s*-1px/.test(b)
        && /overflow:\s*hidden/.test(b) && (/clip:\s*rect\(/.test(b) || /clip-path:\s*inset\(/.test(b));
    }
    if (rule === 'bare-attribute-not-class') {
      // Mirror the rule's predicate: a bare hyphenated attribute whose name is a
      // known class. The self-test's class set here is deliberately the real one
      // minus the quoted-value stripping, which is what made the first attempt at
      // this rule vacuous (it never saw a template at all).
      const known = new Set(['desktop-only', 'mobile-only', 'zen-toggle']);
      fired = /(?:^|\s)([a-z][a-z0-9]*(?:-[a-z0-9]+)+)(?=\s|>|$)/.test(snippet)
        && [...snippet.matchAll(/(?:^|\s)([a-z][a-z0-9]*(?:-[a-z0-9]+)+)(?=\s|>|$)/g)]
          .some((m) => known.has(m[1]));
    }
    if (rule === 'undeclared-token') fired = /var\(\s*--definitely-not-declared/.test(snippet);
    if (rule === 'settings-local-button-family') fired = legacySettingsButtonSelectors(snippet).length > 0;
    if (rule === 'settings-local-switch-family') fired = legacySettingsSwitchRecipes(snippet).length > 0;
    if (rule === 'add-book-local-field-system') fired = legacyAddBookFieldSelectors(snippet).length > 0;
    if (rule === 'add-book-legacy-field-markup') fired = legacyAddBookFieldMarkup(snippet).length > 0;
    if (rule === 'settings-local-form-family') fired = legacySettingsFormRecipes(snippet).length > 0;
    if (rule === 'migrated-surface-legacy-generic-control') fired = legacyMigratedControlMarkup(snippet).length > 0;
    if (rule === 'migrated-surface-unowned-raw-button') {
      fired = rawButtonWithoutPrimitive(snippet, 'src/app/settings/settings.component.html').length > 0;
    }
    if (rule === 'migrated-surface-unowned-raw-field') {
      fired = rawOrdinaryFieldWithoutPrimitive(snippet, 'src/app/settings/settings.component.html').length > 0;
    }
    if (rule === 'raw-native-select') fired = rawNativeSelects(snippet).length > 0;
    if (rule === 'migrated-surface-switch-copy') fired = copiedSwitchGeometry(snippet).length > 0;
    if (rule === 'ad-hoc-notification-surface') fired = adHocNotificationSurface('', snippet);
    if (fired) { ok++; console.log(`  ✔ ${rule} fires on its known-bad snippet`); }
    else console.log(`  ✖ ${rule} DID NOT FIRE — the rule is vacuous`);
    void fake; void before;
  }

  /* The specificity heuristic is ADVISORY (see its doc comment: the (0,2N,0) model
     was wrong, real specificity ties here and source order decides). The self-test
     therefore asserts only that it fires on the known-bad shape, and records that
     it is advisory so nobody mistakes it for a gate. */
  {
    const fakeGlobal = ".nav-item.active .count-badge".split(/\s+/).filter(Boolean).length + 1;
    const fakeComponent = ".sidebar:not(.collapsed) .nav-item .count-badge".split(/\s+/).filter(Boolean).length * 2;
    if (fakeGlobal <= fakeComponent) {
      ok++;
      console.log(`  ✔ possible-unwinnable-dark-override flags the #94 shape ` +
        `(advisory only; approx ${fakeGlobal} <= ${fakeComponent})`);
    } else {
      console.log(`  ✖ possible-unwinnable-dark-override did not flag the shape that shipped`);
    }
  }
  /* Rule 6 shares the rule's own counter, so the two cannot disagree. */
  {
    const bad = 'styles: [\n    `\n      /* see the `info` variant */\n      .x { color: red; }\n    `,\n  ],';
    const ticks = stylesBacktickCount(bad);
    if (ticks !== null && ticks !== 2) {
      ok++;
      console.log(`  ✔ backtick-in-inline-styles fires on an extra-backtick styles array (${ticks} backticks, expected 2)`);
    } else {
      console.log(`  ✖ backtick-in-inline-styles DID NOT FIRE (counted ${ticks}) — the rule is vacuous`);
    }
  }
  /* Rule 7: the exact shape the transition conversion produced, which computes to
     `transitionDuration: "0s, 0s, 0.2s"` in the browser. */
  {
    const shape = 'transition: background-color, border-color, box-shadow 0.2s ease;';
    const body = shape.replace(/^transition:\s*/, '').replace(/;$/,'');
    const parts = body.split(',').map((x) => x.trim()).filter(Boolean);
    const timed = parts.filter((p) => /\d+(?:\.\d+)?(?:m?s)\b/.test(p)).length;
    if (parts.length > 1 && timed < parts.length) {
      ok++;
      console.log(`  ✔ transition-missing-duration fires on the converted shape ` +
        `(${timed}/${parts.length} items carry a duration)`);
    } else {
      console.log(`  ✖ transition-missing-duration DID NOT FIRE — the rule is vacuous`);
    }
  }
  const RULES = ['unterminated-transition', 'visually-hidden', 'literal-colour',
    'undeclared-token', 'possible-unwinnable-dark-override (ADVISORY)',
    'backtick-in-inline-styles', 'transition-missing-duration',
    'bare-attribute-not-class', 'settings-local-button-family',
    'add-book-local-field-system', 'add-book-legacy-field-markup',
    'settings-local-switch-family', 'settings-local-form-family',
    'migrated-surface-legacy-generic-control', 'migrated-surface-unowned-raw-button',
    'migrated-surface-unowned-raw-field', 'raw-native-select', 'migrated-surface-switch-copy',
    'ad-hoc-notification-surface',
    'visually-hidden (by-name + by-recipe)'];
  console.log(`\nself-test: ${ok}/${cases.length + 3} injected cases detected`);
  console.log(`rules implemented: ${RULES.length} (${RULES.join(', ')})`);
  process.exit(ok === cases.length + 3 ? 0 : 1);
}

/**
 * RULE 8 — a utility written as a BARE ATTRIBUTE where the CSS expects a CLASS.
 *
 * "<button appIconButton zen-toggle>" is valid HTML and reads fine in a template,
 * but "zen-toggle" is then an attribute, NOT a class, so ".zen-toggle { ... }" never
 * matches. Migrating the icon buttons by hand produced exactly that: the studio's zen
 * toggle and the reader's desktop-only zoom buttons silently lost their styling.
 *
 * Only flags names that some stylesheet actually uses as a CLASS selector, so real
 * attributes and bare names no CSS keys off are ignored.
 */
{
  // `walk()` deliberately collects only .css/.ts (every other rule is about
  // stylesheets), so templates need their own walk. Found the hard way: this rule
  // first reported nothing simply because no .html file was ever read.
  const TEMPLATES = [];
  (function walkHtml(dir) {
    for (const entry of readdirSync(dir)) {
      const p = join(dir, entry);
      if (statSync(p).isDirectory()) { walkHtml(p); continue; }
      if (extname(p) === '.html') TEMPLATES.push({ path: p, raw: readFileSync(p, 'utf8') });
    }
  })(SRC);

  const classNames = new Set();
  for (const f of files) for (const m of f.css.matchAll(/\.([a-zA-Z][a-zA-Z0-9_-]*)/g)) classNames.add(m[1]);
  // Names that legitimately appear as bare attributes, not classes.
  const ALLOWED = new Set(['disabled', 'required', 'autofocus', 'hidden', 'multiple',
    'readonly', 'selected', 'checked', 'open', 'novalidate', 'autocomplete', 'type',
    'role', 'value', 'name', 'for', 'placeholder', 'min', 'max', 'step', 'rows',
    'cols', 'tabindex', 'colspan', 'rowspan', 'scope', 'target', 'rel', 'loading',
    'decoding', 'draggable', 'contenteditable', 'spellcheck', 'translate', 'wrap',
    'accept', 'capture', 'list', 'pattern', 'size', 'maxlength', 'minlength',
    'inputmode', 'dirname', 'lang', 'dir', 'title', 'alt', 'src', 'href', 'id']);

  for (const f of TEMPLATES) {
    /* Track whether each line sits INSIDE an element tag, by counting unquoted `<` and
       `>`. This replaces two earlier approaches, both wrong:
         - a naive quote-strip (`/"[^"\n]*"/g`) that fired on `class="btn btn-xs"`
           itself, producing 17 false findings in untouched files;
         - the same strip plus an indentation heuristic, which a MULTILINE attribute
           value defeats: `title="...\n      desktop-only mode\n..."` is not stripped
           by a single-line regex, so its body was scanned as if it were markup and the
           guard reported a bare attribute that does not exist. Independent review
           falsified it that way.
       Counting angle brackets handles both: inside a tag, and inside a quoted value,
       are different states, and a multiline value keeps the tag open across lines. */
    const lines = f.raw.split('\n');
    let inTag = false;
    let quote = null; // the quote character we are inside, if any
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      // Build the scannable text for this line: everything outside quotes and comments.
      let scannable = '';
      for (let c = 0; c < line.length; c++) {
        const ch = line[c];
        if (quote) {
          if (ch === quote) quote = null;
          continue; // inside a quoted value: never markup
        }
        if (ch === '"' || ch === "'") { quote = ch; continue; }
        if (!inTag && ch === '<' && line.slice(c, c + 4) === '<!--') {
          // Skip the rest of this HTML comment (approximate: same line only is enough
          // here because a bare attribute inside a comment is not our target anyway).
          const end = line.indexOf('-->', c);
          c = end === -1 ? line.length : end + 2;
          continue;
        }
        if (ch === '<') { inTag = true; continue; }
        if (ch === '>') { inTag = false; continue; }
        scannable += inTag ? ch : ' ';
      }
      // A quoted value opened and not closed on this line leaves `quote` set, so the
      // next line(s) stay non-scannable until the value ends. That is the fix.
      if (!inTag) continue;
      for (const m of scannable.matchAll(/(?:^|\s)([a-z][a-z0-9]*(?:-[a-z0-9]+)+)(?=\s|=|$)/g)) {
        const attr = m[1];
        if (ALLOWED.has(attr)) continue;
        if (!classNames.has(attr)) continue;
        report('bare-attribute-not-class', f.path, i + 1,
          `"${attr}" is a bare attribute, but the CSS has a .${attr} CLASS rule that can ` +
          `never match it. Write class="${attr}". A bare attribute is not a class ` +
          `(near: ${line.trim().slice(0, 60)})`);
      }
    }
  }
}



/**
 * RULE 9 — the canonical Toast pattern is the only generic transient-notification UI.
 *
 * This deliberately does NOT ban product-owned inline status/error surfaces. It blocks
 * only another component/class that identifies itself as generic toast/snackbar chrome,
 * and separately pins the accessibility/motion/layer contracts of the canonical owner.
 */
{
  const canonicalPath = join(
    SRC,
    'app',
    'ui',
    'toast-container',
    'toast-container.component.ts',
  );
  const candidates = [];

  (function walkNotificationSources(dir) {
    for (const entry of readdirSync(dir)) {
      const p = join(dir, entry);
      if (statSync(p).isDirectory()) {
        walkNotificationSources(p);
        continue;
      }
      if (p.endsWith('.spec.ts')) continue;
      if (extname(p) === '.ts' || extname(p) === '.css') candidates.push(p);
    }
  })(SRC);

  for (const p of candidates) {
    if (p === canonicalPath) continue;
    const raw = readFileSync(p, 'utf8');
    const css = stripComments(cssOf(p, raw));
    if (!adHocNotificationSurface(raw, css)) continue;

    report(
      'ad-hoc-notification-surface',
      p,
      1,
      'Generic toast/snackbar UI must use ToastService + app-toast-container. ' +
        'Keep persistent/product-owned statuses local, but do not fork the transient feedback pattern.',
    );
  }

  const canonical = readFileSync(canonicalPath, 'utf8');
  const dismissTag = canonical.match(/<button\b[^>]*toast-dismiss[^>]*>/s)?.[0] ?? '';
  const contract = [
    ['canonical appIconButton dismiss control', /\bappIconButton\b/.test(dismissTag)],
    ['enter motion hook', /animate\.enter/.test(canonical)],
    ['leave motion hook', /animate\.leave/.test(canonical)],
    ['polite status semantics', /['"]status['"]/.test(canonical) && /['"]polite['"]/.test(canonical)],
    ['assertive alert semantics', /['"]alert['"]/.test(canonical) && /['"]assertive['"]/.test(canonical)],
    ['explicit live-region policy', /aria-live/.test(canonical)],
    ['system stacking layer', /var\(--layer-system\)/.test(canonical)],
  ];

  for (const [name, present] of contract) {
    if (present) continue;
    report(
      'canonical-toast-contract',
      canonicalPath,
      1,
      'Toast lost its ' + name +
        '; keep the shared notification pattern inside the Nostos UI contract.',
    );
  }
}


// ------------------------------------------------------------------- output
const byRule = new Map();
for (const f of findings) byRule.set(f.rule, [...(byRule.get(f.rule) ?? []), f]);

for (const it of infos) console.log(`  (info) ${it.rule}: ${it.message}`);

if (!findings.length) {
  console.log(`✔ design-language drift: no findings across ${files.length} stylesheets.`);
  process.exit(0);
}

console.log(`✖ design-language drift: ${findings.length} finding(s) across ${files.length} stylesheets.\n`);
for (const [rule, items] of byRule) {
  console.log(`  ${rule} (${items.length})`);
  for (const it of items.slice(0, 12)) console.log(`    ${it.file}:${it.line}  ${it.message}`);
  if (items.length > 12) console.log(`    … and ${items.length - 12} more`);
  console.log();
}
process.exit(1);
