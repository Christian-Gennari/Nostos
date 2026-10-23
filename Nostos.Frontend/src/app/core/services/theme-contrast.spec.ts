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

/**
 * WCAG AA in the new themes (issue #1074). Light and Dark have their own
 * history and checks; these three were drawn for the whole app here, so every
 * text role is held to AA on every surface it sits on.
 */
describe('Sepia, Mist and Writer\'s Study meet WCAG AA (issue #1074)', () => {
  for (const theme of ['sepia', 'mist', 'study'] as const) {
    describe(theme, () => {
      const t = tokens(theme);
      const c = (token: string) => colour(t, token);

      it('has its own block', () => {
        expect(blockTokens(`:root[data-palette='${theme}']`)['--bg-body'], `${theme} block missing`).toBeTruthy();
      });

      it('keeps body, muted and light text readable on every surface', () => {
        for (const surface of ['--bg-body', '--bg-surface', '--bg-sidebar', '--bg-surface-alt', '--bg-hover']) {
          for (const ink of ['--color-text-main', '--color-text-muted', '--color-text-light']) {
            expect(contrast(c(ink), c(surface)), `${ink} on ${surface}`).toBeGreaterThanOrEqual(4.5);
          }
        }
      });

      it('puts readable text on its primary fill', () => {
        expect(contrast(c('--on-primary'), c('--primary-fill'))).toBeGreaterThanOrEqual(4.5);
      });

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

      it('keeps the focus ring visible (3:1 for UI)', () => {
        expect(contrast(c('--focus-ring'), c('--bg-body'))).toBeGreaterThanOrEqual(3);
        expect(contrast(c('--focus-ring'), c('--bg-surface'))).toBeGreaterThanOrEqual(3);
      });
    });
  }

  it('Sepia stays warm, not yellow: its chrome is low-chroma parchment', () => {
    // The old Studio Sepia ground #fbf3e0 (red - blue = 27) turned the whole
    // app yellow when it was tried app-wide. The chrome stays warm but well
    // under it; only the reading page is allowed the richer cream.
    const t = tokens('sepia');
    for (const token of ['--bg-body', '--bg-surface', '--bg-sidebar', '--bg-surface-alt']) {
      const hex = colour(t, token);
      const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
      // Warm (red ≥ green ≥ blue) and gently so: blue no more than 22 below red.
      expect(r >= g && g >= b, `${token} ${hex} is warm`).toBe(true);
      expect(r - b, `${token} ${hex} is not yellow`).toBeLessThanOrEqual(22);
    }
  });
});
