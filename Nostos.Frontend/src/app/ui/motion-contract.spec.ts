/**
 * Motion-contract gate: a loading skeleton must not come back, and the library
 * must not put anything in the content's place while it loads.
 *
 * Two rules, both learned the hard way:
 *
 * 1. A skeleton is a guessed structure — rows, bars, thumbnails at metrics that
 *    are never quite the real ones — so it jitters against the content it stands
 *    in for. A `skeleton` class, keyframe or markup block reappearing in a
 *    template or stylesheet is a regression, not a style choice.
 *
 * 2. The library goes further: during its cold load the results region is left
 *    EMPTY and simply holds its height, then the real results fade in over it.
 *    Other indeterminate waits use the shared LoadingIndicator primitive: one
 *    localized glyph on the existing surface, never a replacement surface.
 *
 * Scans templates and stylesheets under src/ (component styles included) and
 * reports every offending file. Comments count: naming the thing is how it
 * comes back.
 */
// No @types/node in this repo; vitest resolves the node built-ins at runtime.
// Used only for static source guards (templates/stylesheet files).
// @ts-expect-error — see above.
import { readdirSync, readFileSync, statSync } from 'node:fs';
// @ts-expect-error — see above.
import path from 'node:path';
// @ts-expect-error — see above.
import process from 'node:process';

import { describe, expect, it } from 'vitest';

/** Vitest runs with the project root as cwd (Nostos.Frontend). */
const SRC = path.resolve(process.cwd(), 'src');
const SCANNED_EXTENSIONS = ['.html', '.css', '.scss'];
const FORBIDDEN = /skeleton/i;
const FORBIDDEN_WAIT_FIELD = /wait-field|wait_field/i;

/** Files that must not draw a loading indicator during the library's cold load. */
const LIBRARY_FILES = [
  'app/library/library.component.html',
  'app/library/library.component.css',
];

function collectFiles(dir: string, found: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = path.join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === 'node_modules' || entry === 'dist') continue;
      collectFiles(full, found);
    } else if (SCANNED_EXTENSIONS.includes(path.extname(entry))) {
      found.push(full);
    }
  }
  return found;
}

describe('motion contract', () => {
  it('has no loading skeleton left in any template or stylesheet', () => {
    const files = collectFiles(SRC);

    // Guards the guard: an empty or mis-rooted scan must fail loudly rather
    // than pass as "nothing found" — and the global stylesheet, where shared
    // motion recipes can live, has to be inside the scan.
    expect(files.length, `no templates/stylesheets found under ${SRC}`).toBeGreaterThan(30);
    // Separator-aware: path.join yields backslashes on Windows, where a
    // forward-slash suffix never matches and the guard fails spuriously.
    expect(files.some((file) => file.endsWith(path.join('src', 'styles.css')))).toBe(true);

    const offenders = files
      .filter((file) => FORBIDDEN.test(readFileSync(file, 'utf8')))
      .map((file) => path.relative(SRC, file));

    expect(offenders, 'remove the skeleton and let real content resolve in').toEqual([]);
  });

  it('has no full-area wait field left in any template or stylesheet', () => {
    const files = collectFiles(SRC);
    const offenders = files
      .filter((file) => FORBIDDEN_WAIT_FIELD.test(readFileSync(file, 'utf8')))
      .map((file) => path.relative(SRC, file));

    expect(offenders, 'use the shared LoadingIndicator without repainting the waiting surface').toEqual([]);
  });

  it('draws nothing in the content’s place while the library loads', () => {
    for (const relative of LIBRARY_FILES) {
      const source = readFileSync(path.join(SRC, relative), 'utf8');
      // Present, non-empty files only: a renamed or emptied template must fail
      // rather than pass this guard vacuously.
      expect(source.length, `${relative} looks empty`).toBeGreaterThan(200);
      expect(source, `${relative} must not reference the wait field`).not.toMatch(
        /wait-field|wait_field/,
      );
    }
  });
});
