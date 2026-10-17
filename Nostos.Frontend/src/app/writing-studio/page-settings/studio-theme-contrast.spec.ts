// No @types/node in this repo; vitest resolves node:fs at runtime (angular.json
// lists it under externalDependencies).
// @ts-expect-error — see above.
import { readFileSync } from 'node:fs';
// @ts-expect-error — see above.
import path from 'node:path';
// @ts-expect-error — see above.
import process from 'node:process';

import { STUDIO_THEMES } from './studio-preferences.service';

// The token sets are read from the real stylesheet, so a colour edited there
// is the colour checked here.
// Vitest runs with the project root (Nostos.Frontend) as cwd.
const styles: string = readFileSync(path.resolve(process.cwd(), 'src/styles.css'), 'utf-8');

function tokens(theme: string): Record<string, string> {
  const block = styles.match(new RegExp(`\\.studio-theme-${theme}\\s*\\{([^}]*)\\}`))?.[1] ?? '';
  return Object.fromEntries(
    [...block.matchAll(/(--[\w-]+):\s*(#[0-9a-fA-F]{6})\s*;/g)].map((m) => [m[1], m[2]]),
  );
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

describe('Studio themes meet WCAG AA (issue #1041)', () => {
  const themes = STUDIO_THEMES.map((t) => t.value);

  for (const theme of themes) {
    it(`${theme}: body text and muted UI text are readable on the page and the rails`, () => {
      const t = tokens(theme);
      expect(t['--manuscript-paper'], `${theme} token set missing`).toBeTruthy();
      // The page's body text goes further than AA: long reading wants 7:1.
      expect(contrast(t['--manuscript-ink'], t['--manuscript-paper'])).toBeGreaterThanOrEqual(7);
      // Everything, on the page and on the rails, meets AA (4.5:1).
      for (const surface of [t['--manuscript-paper'], t['--bg-sidebar']]) {
        expect(contrast(t['--manuscript-ink'], surface)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(t['--manuscript-ink-soft'], surface)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(t['--manuscript-ink-faint'], surface)).toBeGreaterThanOrEqual(4.5);
      }
      expect(contrast(t['--manuscript-link'], t['--manuscript-paper'])).toBeGreaterThanOrEqual(4.5);
    });
  }
});
