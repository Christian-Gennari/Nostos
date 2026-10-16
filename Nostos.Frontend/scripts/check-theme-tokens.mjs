#!/usr/bin/env node
/**
 * Theme token-graph guard.
 *
 * A custom property left out of a theme block does NOT error — it silently
 * keeps the OTHER theme's value, which shows up as a light patch (or invisible
 * text) inside the dark UI. That failure mode is how the previous theme system
 * became unmaintainable, and nothing in a light-mode build ever reveals it.
 *
 * Two rules, both the same counterpart comparison:
 *
 *   1. styles.css — the light `:root` set vs the `:root[data-theme='dark']`
 *      set. Fails on any colour token with no counterpart.
 *   2. Theme modules outside styles.css — a `.ts` component that injects its own
 *      document (currently the TinyMCE editor content in `markdown-editor`)
 *      declares its own light and dark blocks. Those were previously invisible
 *      to this script, so a token added to only one of their two blocks would
 *      silently keep the light value on dark and nothing would catch it.
 *
 * Usage:  node scripts/check-theme-tokens.mjs [path/to/theme-module.(mjs|ts)]
 * Exit 1 with a list of missing tokens, or 0 when the graph is complete.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join, extname } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const srcRoot = resolve(here, '../src');
const file = process.argv[2] ?? join(srcRoot, 'styles.css');
const css = readFileSync(file, 'utf8');
const rawSource = css;

/**
 * Non-colour token families. These are theme-INVARIANT by design (geometry,
 * motion, type scale, spacing) so they are expected to be absent from the dark
 * block and must not be reported.
 *
 * `--brand-*` is here because the brand mark is deliberately ONE variant in both
 * themes (forest tile / paper arch). It is a colour, but not a *themed* colour:
 * the mark does not re-ink with the theme, so demanding a dark counterpart would
 * push a needless override back into the dark block.
 *
 * `--highlight-*` is here for the same reason: those are the reader's highlighter
 * pens, drawn on the reading surface, and that surface is paper in every theme
 * (the EPUB iframe and the PDF page are the book's own light page — which is why
 * the themed `--color-highlight` is a pale sage on dark). Three per-theme copies
 * would be three ways to state one value.
 */
const INVARIANT = /^--(radius|motion|ease|text-(xs|sm|base|lg|xl|2xl)|space|container-width|fw|transition|glass-blur|modal-scrim-blur|sidebar-width|brand-|highlight-|control-h|focus-ring-width|dock-rail-h|dock-float-bottom|dock-bar-h)/;

/**
 * Tokens whose value is a `color-mix()` of another token. They re-derive
 * themselves per theme, so an explicit dark value is not required.
 */
// `--control-border-hover` and `--focus-halo*` are mixes of `--control-border`,
// `--focus-ring` and `--color-danger`, each of which already has a dark value.
const DERIVED = /^--(danger-|control-border-hover$|focus-halo)/;

/**
 * Extract custom properties from every light `:root { ... }` block.
 * `styles.css` declares several (surfaces, then type/shadows/glass, then editor
 * chrome), so scanning only the first would silently under-report. Blocks
 * qualified with `[data-theme=...]` are skipped: those belong to a theme.
 */
function lightTokens(source) {
  const re = /(^|[\s}])?:root\s*\{/g;
  const out = new Map();
  let m;
  let found = 0;
  while ((m = re.exec(source))) {
    const brace = source.indexOf('{', m.index + (m[0].length - 1));
    // Skip `:root[data-theme=...]` — a themed block, not the light default.
    const selectorStart = source.lastIndexOf('\n', m.index) + 1;
    if (/data-theme/.test(source.slice(selectorStart, brace))) continue;
    const end = matchBlockEnd(source, brace);
    const block = source.slice(brace, end);
    for (const t of block.matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)) out.set(t[1], t[2].trim());
    found++;
  }
  if (!found) throw new Error('no plain `:root {` block found');
  return out;
}

/** Index of the `}` closing the block that opens at `openIndex`. */
function matchBlockEnd(source, openIndex) {
  let depth = 0;
  for (let i = openIndex; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) return i;
    }
  }
  throw new Error('unbalanced braces');
}

/** Extract custom properties from the `:root[data-theme='dark']` block. */
function darkTokens(source) {
  const m = source.match(/:root\[data-theme=['"]dark['"]\]\s*\{/);
  if (!m) throw new Error("no `:root[data-theme='dark']` block found");
  const brace = source.indexOf('{', m.index);
  const end = matchBlockEnd(source, brace);
  return new Map(
    [...source
      .slice(brace, end)
      .matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)].map((x) => [x[1], x[2].trim()]),
  );
}

/**
 * Theme-invariant modules: stylesheets that live outside styles.css and own
 * both of their theme blocks locally.
 *
 * `src/app/ui/markdown-editor/markdown-editor.component.ts` injects a document
 * into a TinyMCE iframe. That document is self-contained: 13 tokens declared in
 * its own `:root` with all 13 repeated in its own `:root[data-theme='dark']`,
 * and it borrows nothing from the global graph. It is therefore NOT a leak —
 * but until now it was also unguarded, so a token added to only one of its two
 * blocks would silently keep the light value on dark and nothing would notice.
 *
 * This parses those modules with the SAME counterpart rule. It deliberately
 * does not require the token namespaces to agree with styles.css: the editor
 * content is a separate visual world (warm ink on paper) and its `--ink`/
 * `--paper` vocabulary is local by design, not a duplicate of the app's.
 */
function inlineStyleModules(source) {
  const modules = [];
  for (const m of source.matchAll(/const\s+(\w*CSS\w*)\s*=\s*`([\s\S]*?)`;/g)) {
    const [, name, body] = m;
    const hasLight = /:root\s*\{/.test(body);
    const hasDark = /:root\[data-theme=['"]dark['"]\]\s*\{/.test(body);
    if (!hasLight && !hasDark) continue;
    modules.push({ name, body });
  }
  return modules;
}

const light = lightTokens(css);
const dark = darkTokens(css);

const missing = [];
for (const [token, value] of light) {
  if (dark.has(token)) continue;
  if (INVARIANT.test(token)) continue;
  if (DERIVED.test(token)) continue;
  missing.push([token, value]);
}

const introduced = [...dark.keys()].filter((t) => !light.has(t));

console.log(`${file}`);
console.log(`  light tokens        : ${light.size}`);
console.log(`  dark tokens         : ${dark.size}`);
console.log(`  dark-only (new)     : ${introduced.length ? introduced.join(', ') : '—'}`);

// Same rule, applied to every theme module found outside styles.css.
// These live in `.ts` files (a component injecting its own document), which is
// why reading only styles.css missed them entirely.
function collectTs(dir, found = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === 'dist' || entry.startsWith('.')) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) collectTs(full, found);
    else if (extname(entry) === '.ts' && !entry.endsWith('.spec.ts')) found.push(full);
  }
  return found;
}

const moduleFailures = [];
const modulesSeen = [];
for (const tsFile of collectTs(srcRoot)) {
  const source = readFileSync(tsFile, 'utf8');
  for (const { name, body } of inlineStyleModules(source)) {
    const ml = tokenSetInBlock(body, /:root\s*\{/);
    const md = tokenSetInBlock(body, /:root\[data-theme=['"]dark['"]\]\s*\{/);
    if (!ml.size) continue;
    const rel = tsFile.replace(srcRoot + '/', '');
    modulesSeen.push(name);
    const miss = [...ml.keys()].filter(
      (t) => !md.has(t) && !INVARIANT.test(t) && !DERIVED.test(t),
    );
    console.log(`  module ${name.padEnd(26)} ${String(ml.size).padStart(2)} light / ${String(md.size).padStart(2)} dark   (${rel})`);
    if (miss.length) moduleFailures.push({ name, rel, miss });
  }
}

// Non-vacuity. The editor content module is the one known theme module; if the
// scan stops finding it (renamed constant, refactored away, or a mis-rooted
// walk) this guard would silently pass by finding nothing to check.
const KNOWN_THEME_MODULE = 'NOSTOS_EDITOR_CONTENT_CSS';
if (!modulesSeen.includes(KNOWN_THEME_MODULE)) {
  console.error(`\n✖ theme-module scan did not find ${KNOWN_THEME_MODULE}.`);
  console.error(`  Found: ${modulesSeen.length ? modulesSeen.join(', ') : '(none)'}`);
  console.error('  The scan is mis-rooted or the module was renamed — this guard would pass vacuously.\n');
  process.exit(1);
}

if (moduleFailures.length) {
  console.error(`\n✖ ${moduleFailures.length} theme module(s) have an incomplete counterpart set.`);
  for (const { name, rel, miss } of moduleFailures) {
    console.error(`\n  ${name}  (${rel}) is missing a dark counterpart for:`);
    for (const t of miss) console.error(`    ${t}`);
  }
  console.error('\n  A token left out of one block silently keeps the OTHER theme value.\n');
  process.exit(1);
}

/**
 * Read the custom properties declared in the first block matching `blockRe`,
 * jumping to the block's own closing brace (a nested `{` must not end it).
 */
function tokenSetInBlock(source, blockRe) {
  const m = source.match(blockRe);
  if (!m) return new Map();
  const brace = source.indexOf('{', m.index);
  let depth = 0;
  let end = brace;
  for (let i = brace; i < source.length; i++) {
    if (source[i] === '{') depth++;
    else if (source[i] === '}') {
      depth--;
      if (depth === 0) { end = i; break; }
    }
  }
  return new Map(
    [...source.slice(brace, end).matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)].map((x) => [x[1], x[2].trim()]),
  );
}

if (moduleFailures.length) {
  console.error(`\n✖ ${moduleFailures.length} theme-invariant module(s) have an incomplete counterpart set.`);
  for (const { name, miss } of moduleFailures) {
    console.error(`\n  ${name} is missing a dark counterpart for:`);
    for (const t of miss) console.error(`    ${t}`);
  }
  console.error('\n  A token left out of one block silently keeps the OTHER theme value.\n');
  process.exit(1);
}

if (missing.length) {
  console.error(`\n✖ ${missing.length} colour token(s) have no dark counterpart.`);
  console.error('  Each will silently keep its LIGHT value inside the dark theme:\n');
  for (const [token, value] of missing) console.error(`    ${token.padEnd(30)} ${value}`);
  console.error('\n  Add these to the `:root[data-theme=\'dark\']` block in styles.css,');
  console.error('  or add the family to INVARIANT if it is genuinely theme-invariant.\n');
  process.exit(1);
}

console.log('\n✔ Every colour token has a dark counterpart, in styles.css and in every theme module.\n');
