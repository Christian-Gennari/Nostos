// vitest resolves node:fs at runtime (angular.json lists it under
// externalDependencies).
import { readFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import { THEMES, ThemeName } from './theme.service';

// The token sets are read from the real stylesheet, so a colour edited there
// is the colour checked here.
// Vitest runs with the project root (Nostos.Frontend) as cwd.
const styles: string = readFileSync(path.resolve(process.cwd(), 'src/styles.css'), 'utf-8');

/** The custom properties of every block opened by `selector` (a `{` follows it). */
function blockTokens(selector: string): Record<string, string> {
  const out: Record<string, string> = {};
  let from = 0;
  for (;;) {
    const at = styles.indexOf(selector, from);
    if (at < 0) return out;
    from = at + selector.length;
    // Only a block whose selector is exactly this one: `:root {` must not take
    // `:root[data-theme='dark'] {`.
    if (!/^\s*\{/.test(styles.slice(from))) continue;
    const open = styles.indexOf('{', from);
    const body = styles.slice(open + 1, styles.indexOf('\n}', open)).replace(/\/\*[\s\S]*?\*\//g, '');
    for (const m of body.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) out[m[1]] = m[2].trim();
  }
}

/** A theme's resolved tokens: its mode's base, then its own palette on top. */
function tokens(theme: ThemeName): Record<string, string> {
  const mode = THEMES.find((t) => t.value === theme)!.mode;
  const set = { ...blockTokens(':root') };
  if (mode === 'dark') Object.assign(set, blockTokens(":root[data-theme='dark']"));
  if (theme !== 'light' && theme !== 'dark') Object.assign(set, blockTokens(`:root[data-palette='${theme}']`));
  return set;
}

/** Follows `var(--x)` (and `var(--x, fallback)`) to a hex colour. */
function colour(set: Record<string, string>, token: string): string {
  let value = set[token];
  for (let i = 0; i < 6 && value?.startsWith('var('); i++) value = set[value.slice(4).split(/[,)]/)[0].trim()];
  if (!value || !/^#[0-9a-f]{6}$/i.test(value)) throw new Error(`${token} is not a plain hex colour: ${value}`);
  return value;
}

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const f = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const BRAIN_SURFACES = [
  '--bg-pane-nav',
  '--bg-pane-list',
  '--bg-pane-reader',
  '--row-hover',
  '--row-selected',
] as const;

const CORE_SURFACES = [
  '--bg-body',
  '--bg-surface',
  '--bg-sidebar',
  '--bg-surface-alt',
  '--bg-hover',
  ...BRAIN_SURFACES,
] as const;

/**
 * WCAG AA across all five app themes (issues #1074, #1808). Every text role
 * is held to 4.5:1 on every surface it sits on, including the Brain's three
 * pane surfaces and two row states, and the focus ring holds 3:1 on all of them.
 */
describe('All five themes meet WCAG AA on core and Brain surfaces (issues #1074, #1808)', () => {
  for (const theme of ['light', 'sepia', 'mist', 'study', 'dark'] as const) {
    describe(theme, () => {
      const t = tokens(theme);
      const c = (token: string) => colour(t, token);

      if (theme !== 'light' && theme !== 'dark') {
        it('has its own block', () => {
          expect(blockTokens(`:root[data-palette='${theme}']`)['--bg-body'], `${theme} block missing`).toBeTruthy();
        });
      }

      it('keeps body, muted and light text readable on every surface, pane and row state', () => {
        for (const surface of CORE_SURFACES) {
          for (const ink of ['--color-text-main', '--color-text-muted', '--color-text-light']) {
            expect(contrast(c(ink), c(surface)), `${theme}: ${ink} on ${surface}`).toBeGreaterThanOrEqual(4.5);
          }
        }
      });

      it('puts readable text on its primary fill', () => {
        expect(contrast(c('--on-primary'), c('--primary-fill'))).toBeGreaterThanOrEqual(4.5);
      });

      if (theme === 'sepia' || theme === 'mist' || theme === 'study') {
        it('gives the page 7:1 body text and AA everywhere else', () => {
          // Long reading wants 7:1.
          expect(contrast(c('--manuscript-ink'), c('--manuscript-paper'))).toBeGreaterThanOrEqual(7);
          for (const surface of [c('--manuscript-paper'), c('--bg-sidebar')]) {
            for (const ink of ['--manuscript-ink', '--manuscript-ink-soft', '--manuscript-ink-faint']) {
              expect(contrast(c(ink), surface), `${ink}`).toBeGreaterThanOrEqual(4.5);
            }
          }
          expect(contrast(c('--manuscript-link'), c('--manuscript-paper'))).toBeGreaterThanOrEqual(4.5);
        });
      }

      it('keeps the focus ring visible (3:1 for UI) on every pane and row state', () => {
        for (const surface of ['--bg-body', '--bg-surface', ...BRAIN_SURFACES]) {
          expect(contrast(c('--focus-ring'), c(surface)), `${theme}: --focus-ring on ${surface}`).toBeGreaterThanOrEqual(3);
        }
      });
    });
  }

  it('Sepia stays warm, not yellow: its chrome is low-chroma parchment', () => {
    // The old Studio Sepia ground #fbf3e0 (red - blue = 27) turned the whole
    // app yellow when it was tried app-wide. The chrome stays warm but well
    // under it; only the reading page is allowed the richer cream.
    const t = tokens('sepia');
    for (const token of ['--bg-body', '--bg-surface', '--bg-sidebar', '--bg-surface-alt', ...BRAIN_SURFACES]) {
      const hex = colour(t, token);
      const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
      // Warm (red ≥ green ≥ blue) and gently so: blue no more than 22 below red.
      expect(r >= g && g >= b, `${token} ${hex} is warm`).toBe(true);
      expect(r - b, `${token} ${hex} is not yellow`).toBeLessThanOrEqual(22);
    }
  });
});
