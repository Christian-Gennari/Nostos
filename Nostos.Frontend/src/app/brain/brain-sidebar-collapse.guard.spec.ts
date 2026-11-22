/**
 * The Brain sidebar's collapse contract (#1784).
 *
 * The panel has two layouts and only one of them is a rail: on desktop the
 * shell column clips it to 72px, on a phone the whole drawer slides in. The
 * drawer opens from its own state (`state.drawerOpen`), while `.collapsed`
 * still follows the desktop preference (`preferences.sidebarExpanded`), so a
 * drawer opened from a collapsed desktop session carries `.collapsed`. Every
 * rail-only effect therefore has to be undone inside the ≤768px block: the
 * labels, counts, brand word and Topics group already were; the selected pill's
 * width was not, and the selected place painted a 40px square behind its icon
 * with the name and count on the panel ground beside it.
 *
 * jsdom applies no media queries, so this cannot be observed on a rendered
 * element in the unit suite; the check is on the stylesheet itself. What it
 * pins is exactly what broke: the rail's square above 768px, the full row
 * inside the drawer block.
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import { describe, expect, it } from 'vitest';

const PILL = '.sidebar.collapsed .nav-item::before';

/** Comments are stripped so a rule quoted in prose can never satisfy a check. */
const CSS = readFileSync(
  path.resolve(process.cwd(), 'src/app/brain/brain-sidebar.component.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

/** The body of the block whose opening brace is at `open`. */
function blockAt(source: string, open: number): string {
  let depth = 0;
  for (let i = open; i < source.length; i += 1) {
    if (source[i] === '{') depth += 1;
    else if (source[i] === '}') {
      depth -= 1;
      if (depth === 0) return source.slice(open + 1, i);
    }
  }
  throw new Error('unbalanced braces in brain-sidebar.component.css');
}

/** The phone drawer block. */
function drawerBlock(): { start: number; end: number; body: string } {
  const query = /@media\s*\(max-width:\s*768px\)/.exec(CSS);
  if (!query) throw new Error('the phone drawer block is gone');
  const start = CSS.indexOf('{', query.index);
  const body = blockAt(CSS, start);
  return { start, end: start + body.length + 1, body };
}

/** The width a selector declares in `scope`, or null when it declares none. */
function declaredWidth(scope: string, selector: string): string | null {
  for (const rule of scope.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selectors = rule[1].split(',').map((part) => part.trim().replace(/\s+/g, ' '));
    if (!selectors.includes(selector)) continue;
    const width = /(?:^|;)\s*width\s*:\s*([^;]+)/.exec(rule[2]);
    if (width) return width[1].trim();
  }
  return null;
}

function desktopOnly(): string {
  const drawer = drawerBlock();
  return CSS.slice(0, drawer.start) + CSS.slice(drawer.end);
}

describe('Brain sidebar collapse contract', () => {
  it('keeps the selected pill as the 40px rail square on desktop', () => {
    expect(declaredWidth(desktopOnly(), PILL)).toBe('40px');
  });

  it('spans the selected pill across the row in the phone drawer', () => {
    const drawer = drawerBlock();
    // The drawer shows the name and the count, so the tint has to reach them.
    expect(drawer.body).toMatch(/\.sidebar\.collapsed \.label[\s\S]*?opacity:\s*1/);
    expect(declaredWidth(drawer.body, PILL)).toBe('100%');
  });
});
