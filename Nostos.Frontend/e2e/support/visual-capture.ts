/**
 * Visual-verification capture helpers — reusable surface of the visual-QA
 * harness (see ../visual-regression.spec.ts and docs/visual-verification.md).
 *
 * Everything here is additive: it reads the app's rendered DOM and the
 * fixture state, and writes evidence artifacts (PNG + geometry JSON) under
 * e2e/visual-evidence/. It never modifies application code, the Playwright
 * config, or the shared fixture lifecycle.
 *
 * Artifact naming follows the fixed-light matrix in docs/visual-verification.md:
 * <surface>-<state>-<viewport>.png, e.g. epub-light-desktop.png. All captures
 * are the app's ONE fixed light rendering — the theme system is gone, so the
 * harness never parameterizes by theme and never clicks theme controls.
 * Viewports are exactly 1440x900 (desktop) and 390x844 (mobile) and PNGs are
 * captured at deviceScaleFactor 1 so the artifact dimensions are exact.
 *
 * Real-library mode: reader surfaces (EPUB/PDF) need a book file, which the
 * isolated e2e fixture cannot provide (no test assets exist in-repo; they are
 * never invented). Set VISUAL_QA_LIBRARY_URL to a running instance that has
 * real books AND serves the build under test; the harness then captures the
 * full 10-image matrix. Without it, reader surfaces skip with a clear message.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import type { Browser, Page } from '@playwright/test';

// ---------------------------------------------------------------------------
// Types + constants
// ---------------------------------------------------------------------------

export interface Viewport {
  width: number;
  height: number;
}

/** Protocol viewports (expert section 4): exactly these, nothing else. */
export const DESKTOP_VIEWPORT: Viewport = { width: 1440, height: 900 };
export const MOBILE_VIEWPORT: Viewport = { width: 390, height: 844 };

/**
 * Phone in landscape. A 334px-tall reading area cannot hold the column
 * composition at any useful size, so the audio player composes in two columns
 * here and its own contract differs from both the desktop bar and the portrait
 * one (see checkAudioComposition).
 */
export const LANDSCAPE_VIEWPORT: Viewport = { width: 844, height: 390 };

/**
 * Light-theme expectations for EPUB publisher-CSS normalization. These values
 * are scoped to the EPUB assertion, not the app's general light/dark theme.
 * They mirror epub-reader.component.ts NOSTOS_LIGHT_RULES and the :root tokens
 * in styles.css. If these values change, update the assertion and docs
 * (docs/visual-verification.md §Fixed rendering invariants) in the same PR.
 */
export const READER_IFRAME_LIGHT = { background: '#ffffff', color: '#1a1a1a' } as const;

/** Shell surface (--bg-surface tokens; #epub-viewer uses it). */
export const READER_SHELL_LIGHT = '#ffffff';

/**
 * Target library filter contract (expert section 3): the sidebar is the
 * single progress-filter home with exactly these six choices and NO toolbar
 * progress surface. The contract is enforced once the progress-filter repair
 * lands on main; until then the check reports a documented skip (see
 * docs/visual-verification.md §Library).
 */
export const LIBRARY_FILTER_CONTRACT = [
  'All Books',
  'Not Started',
  'In Progress',
  'Favorites',
  'Finished',
  'Unsorted',
] as const;

/** Optional real-library endpoint for reader surfaces (see header comment). */
export const LIBRARY_URL = (process.env.VISUAL_QA_LIBRARY_URL ?? '').replace(/\/+$/, '');

export const READER_SKIP_REASON =
  'reader surfaces require real book files, which the isolated e2e fixture cannot ' +
  'provide (no EPUB/PDF test assets exist in-repo and the harness never invents them). ' +
  'Run against a real library instance with `VISUAL_QA_LIBRARY_URL=<origin>` to capture ' +
  'the EPUB/PDF images (see docs/visual-verification.md §Reader surfaces).';

export interface GeometryCheck {
  id: string;
  pass: boolean;
  /** true when the check is a documented skip (dependency not present), never a failure. */
  skipped?: boolean;
  message: string;
  metrics?: Record<string, unknown>;
}

export interface CaptureMeta {
  name: string;
  surface: 'epub' | 'pdf' | 'audio' | 'studio' | 'library' | 'book-detail' | 'brain';
  viewport: Viewport;
  state: string;
}

/**
 * Bounds for a rendered node's size, in screen pixels.
 *
 * These are SIGMA node sizes (the graph's `size` attribute), not the radii the
 * pre-rewrite SVG circles carried: the map draws its smallest topic at 4px and
 * its largest at 16px. The old 14-34 range described the SVG circles and made
 * every node fail the check once the map moved to Sigma.
 */
export const BRAIN_MAP_NODE_RADIUS = { min: 3, max: 20 } as const;

// ---------------------------------------------------------------------------
// Evidence artifacts
// ---------------------------------------------------------------------------

/** e2e/visual-evidence/ — the repo's tracked evidence directory. */
export function evidenceDir(): string {
  const dir = path.join(__dirname, '..', 'visual-evidence');
  mkdirSync(dir, { recursive: true });
  return dir;
}

export function artifactPath(name: string, ext: 'png' | 'json'): string {
  return path.join(evidenceDir(), `${name}.${ext}`);
}

/** Captures the viewport (exact protocol size) as <name>.png. */
export async function capturePng(page: Page, name: string): Promise<string> {
  const out = artifactPath(name, 'png');
  await page.screenshot({ path: out });
  return out;
}

/** Writes the geometry report <name>.json: one check list per capture. */
export async function writeGeometryReport(
  name: string,
  checks: GeometryCheck[],
  meta: CaptureMeta
): Promise<string> {
  const report = {
    artifact: `${name}.png`,
    capturedAt: new Date().toISOString(),
    ...meta,
    checks,
    summary: {
      total: checks.length,
      passed: checks.filter((c) => c.pass && !c.skipped).length,
      skipped: checks.filter((c) => c.skipped).length,
      failed: checks.filter((c) => !c.pass && !c.skipped).length,
    },
  };
  const out = artifactPath(name, 'json');
  writeFileSync(out, JSON.stringify(report, null, 2) + '\n');
  return out;
}

// ---------------------------------------------------------------------------
// Page setup
// ---------------------------------------------------------------------------

/**
 * Opens a fresh context at the protocol viewport. The app ships exactly one
 * (light) rendering, so no theme state is pre-seeded. mobile emulation adds
 * touch/DPR-1 so artifact pixels are exact.
 */
export async function newCapturePage(
  browser: Browser,
  viewport: Viewport,
  mobile = false
): Promise<{ context: Awaited<ReturnType<Browser['newContext']>>; page: Page }> {
  const context = await browser.newContext({
    viewport,
    isMobile: mobile,
    hasTouch: mobile,
    deviceScaleFactor: 1, // exact protocol pixel dimensions in the PNG
  });
  const page = await context.newPage();
  return { context, page };
}

// ---------------------------------------------------------------------------
// Geometry checks (expert section 4, "Automated Playwright geometry checks")
// ---------------------------------------------------------------------------

function passCheck(id: string, message: string, metrics?: Record<string, unknown>): GeometryCheck {
  return { id, pass: true, message, metrics };
}

function failCheck(id: string, message: string, metrics?: Record<string, unknown>): GeometryCheck {
  return { id, pass: false, message, metrics };
}

/**
 * EPUB: the rendition iframe's foreground/background must equal the fixed
 * light normalization constants, and the #epub-viewer shell surface must
 * match the same light surface so no pale rim separates shell from content.
 * These are fixed rendering invariants — the app ships exactly one theme.
 */
export async function checkEpubIframeLight(page: Page): Promise<GeometryCheck> {
  const expected = READER_IFRAME_LIGHT;
  const shellExpected = READER_SHELL_LIGHT;
  const frameLocator = page.frameLocator('#epub-viewer iframe');
  const body = frameLocator.locator('body').first();
  await body.waitFor({ timeout: 30_000 });

  const contentStyles = await body.evaluate(() => {
    const hex = (rgb: string): string => {
      const m = /rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/.exec(rgb);
      if (!m) return rgb.trim().toLowerCase();
      return `#${[m[1], m[2], m[3]].map((v) => Number(v).toString(16).padStart(2, '0')).join('')}`;
    };
    const cs = (el: Element) => getComputedStyle(el);
    return {
      bodyBg: hex(cs(document.body).backgroundColor),
      bodyColor: hex(cs(document.body).color),
    };
  });
  const shellBg = await page
    .locator('#epub-viewer')
    .evaluate((el) => {
      const m = /rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/.exec(getComputedStyle(el).backgroundColor);
      return m
        ? `#${[m[1], m[2], m[3]].map((v) => Number(v).toString(16).padStart(2, '0')).join('')}`
        : getComputedStyle(el).backgroundColor.trim().toLowerCase();
    });

  const bodyBg = contentStyles.bodyBg;
  const bodyColor = contentStyles.bodyColor;
  const ok = bodyBg === expected.background && bodyColor === expected.color && shellBg === shellExpected;
  return ok
    ? passCheck(
        'epub-iframe-light',
        `iframe ${bodyBg}/${bodyColor} and shell ${shellBg} match the fixed light invariants ` +
          `(${expected.background}/${expected.color}/${shellExpected})`,
        { bodyBg, bodyColor, shellBg, expected }
      )
    : failCheck(
        'epub-iframe-light',
        `iframe/shell colors deviate from the fixed light rendering: got ${bodyBg}/${bodyColor} ` +
          `(shell ${shellBg}), expected ${expected.background}/${expected.color} (shell ${shellExpected})`,
        { bodyBg, bodyColor, shellBg, expected }
      );
}

/**
 * PDF: the immersive toolbar overlays the scrollport instead of shortening it.
 * At the final scroll position, reveal that toolbar and verify the LAST PAGE
 * itself can scroll completely above it. This proves the permanent scroll
 * padding protects document content without making chrome participate in
 * layout (#759).
 */
export async function checkPdfFinalPageClearance(page: Page): Promise<GeometryCheck> {
  const scrollport = page.locator('#viewerContainer');
  await scrollport.waitFor({ timeout: 30_000 });
  const pages = page.locator('#viewerContainer .page');
  await pages.first().waitFor({ timeout: 30_000 });
  await scrollport.evaluate((el) => {
    el.scrollTop = el.scrollHeight;
  });
  await page.waitForTimeout(400); // let pdf.js settle after the jump

  const chrome = page.getByTestId('reader-chrome-top');
  if ((await chrome.getAttribute('aria-hidden')) === 'true') {
    await page.locator('.pdf-container').click({ position: { x: 160, y: 160 } });
    await page.getByTestId('reader-chrome-bottom').waitFor({ state: 'visible' });
  }

  const lastPage = await pages.last().boundingBox();
  const tb = await page.locator('header.reader-toolbar').boundingBox();
  if (!lastPage || !tb) {
    return failCheck('pdf-page-clearance', 'could not measure final-page/toolbar geometry', {
      lastPage,
      tb,
    });
  }

  const pageBottom = lastPage.y + lastPage.height;
  const clearance = tb.y - pageBottom;
  const ok = clearance >= -0.5;
  const msg =
    `final page bottom ${pageBottom.toFixed(1)}px vs overlay toolbar top ${tb.y.toFixed(1)}px ` +
    `-> clearance ${clearance.toFixed(1)}px (${ok ? 'clear' : 'OVERLAPPED'})`;
  return ok
    ? passCheck('pdf-page-clearance', msg, { clearance })
    : failCheck('pdf-page-clearance', msg, { clearance });
}

/**
 * Audio surface composition (issue #227, plus the phone passes).
 *
 * Desktop: the player must own exactly one chrome row inside itself, leave no
 * dead band larger than the issue's ~48px bar around the composition, and never
 * exceed the viewport. All three were defects: 12 controls in two stacked rows,
 * 164px bands above and below, and a container that computed 30px wider than a
 * 390px phone.
 *
 * Phone (<=768px wide, or <=520px tall for landscape): "fill the reading area" is
 * the wrong criterion — it produced a 320x480 cover in a 792px area that scrolled
 * and clipped its own Playback pill at every phone size measured (0 scroll at
 * 844, but 52px at 730, 78px at 640, 89px at 568, with the pill below the fold),
 * and in landscape it could not fit at all. What is asserted instead is that the
 * composition FITS: the reading area does not scroll, nothing is pushed above its
 * top edge, no horizontal overflow, and the transport trio stays on one line.
 * Then, per composition: PORTRAIT stacks the Playback control under the transport
 * (the owner's call, asserted as 2 control rows with a non-negative gap), keeps
 * the cover under 58% of the area; SHORT LANDSCAPE puts transport and pill back
 * inline in the right-hand column (1 row, cover under 60%, and the cover's right
 * edge must stay clear of the controls column). Measured numbers are reported
 * either way, so a re-bloat and a shrink-to-nothing are both visible.
 */
export async function checkAudioComposition(page: Page): Promise<GeometryCheck> {
  await page.locator('.audio-container').waitFor({ timeout: 30_000 });
  await page.locator('.cover-art').waitFor({ timeout: 30_000 });
  await page.waitForTimeout(400); // let the art and time labels settle

  const vp = page.viewportSize();
  const phone = (vp?.width ?? 0) <= 768 || (vp?.height ?? 0) <= 520;

  const m = await page.evaluate(() => {
    const cont = document.querySelector('.audio-container') as HTMLElement | null;
    if (!cont) return null;
    const host = cont.parentElement as HTMLElement | null;
    const kids = [...cont.children].filter(
      (e) => (e as HTMLElement).getClientRects().length > 0
    ) as HTMLElement[];
    if (kids.length < 2) return null;
    const cr = cont.getBoundingClientRect();
    const first = kids[0].getBoundingClientRect();
    const last = kids[kids.length - 1].getBoundingClientRect();
    // Rows are counted by OVERLAP, not by element count and not by distinct
    // `top`s: `align-items: center` gives a shorter child its own `top` on the
    // same row, and counting elements reported "1 row" while the Playback pill
    // had in fact wrapped onto a second one on every phone width.
    const spans = [...cont.querySelectorAll('.playback-row > *')]
      .map((e) => (e as HTMLElement).getBoundingClientRect())
      .map((r) => [r.top, r.bottom] as [number, number])
      .sort((a, b) => a[0] - b[0]);
    let chromeRows = 0;
    let cursor = -Infinity;
    for (const [top, bottom] of spans) {
      if (top >= cursor) chromeRows++;
      cursor = Math.max(cursor, bottom);
    }
    const cover = document.querySelector('.cover-art')?.getBoundingClientRect();
    const controls = document.querySelector('.controls')?.getBoundingClientRect();
    const transport = document.querySelector('.main-controls')?.getBoundingClientRect();
    const selector = document.querySelector('.playback-selector')?.getBoundingClientRect();
    // The transport trio must stay on ONE line in every layout — it is the row
    // that predates this pass and the one the thumb reaches for.
    let transportRows = 0;
    let tCursor = -Infinity;
    for (const r of [...cont.querySelectorAll('.main-controls > *')]
      .map((e) => (e as HTMLElement).getBoundingClientRect())
      .sort((a, b) => a.top - b.top)) {
      if (r.top >= tCursor) transportRows++;
      tCursor = Math.max(tCursor, r.bottom);
    }
    return {
      top: Math.round(first.top - cr.top),
      bottom: Math.round(cr.bottom - last.bottom),
      chromeRows,
      transportRows,
      overflowX: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      scrollable: host ? host.scrollHeight - host.clientHeight : 0,
      areaHeight: Math.round(cr.height),
      coverShare: cover ? +(cover.height / cr.height).toFixed(3) : 0,
      flexDirection: getComputedStyle(cont).flexDirection,
      columnGapPx: cover && controls ? Math.round(controls.left - cover.right) : 0,
      // Portrait stacks the Playback control under the transport (owner's call);
      // this is the measurement that proves it is stacked and not overlapping.
      pillGapPx: transport && selector ? Math.round(selector.top - transport.bottom) : 0,
      timeText: (document.querySelector('.time-labels')?.textContent ?? '').trim().replace(/\s+/g, ' '),
    };
  });

  if (!m) return failCheck('audio-composition', 'could not measure .audio-container', {});
  const worst = Math.max(m.top, m.bottom);
  const row = m.flexDirection === 'row';
  const ok = phone
    ? m.scrollable <= 1 &&
      m.top >= 0 &&
      m.transportRows === 1 &&
      m.overflowX === 0 &&
      (row
        ? m.chromeRows === 1 && m.coverShare <= 0.6 && m.columnGapPx >= -2
        : m.chromeRows === 2 && m.pillGapPx >= -2 && m.coverShare <= 0.58)
    : worst <= 48 && m.chromeRows === 1 && m.overflowX === 0;
  const axes = row
    ? `${m.chromeRows} row (transport + pill inline, 1 expected), cover ${m.coverShare} of the ` +
      `${m.areaHeight}px area (bar 0.6), gap to the controls column ${m.columnGapPx}px (must be >= -2)`
    : `transport rows ${m.transportRows} (1 expected), ${m.chromeRows} control rows ` +
      `(2 expected: Playback stacked under the transport, gap ${m.pillGapPx}px, must be >= -2), ` +
      `cover ${m.coverShare} of the ${m.areaHeight}px area (bar 0.58)`;
  const msg = phone
    ? `phone fit — ${row ? 'two columns' : 'one column'}: scroll ${m.scrollable}px (bar 1), ` +
      `dead band top ${m.top}px (must be >= 0), ${axes}, ` +
      `overflowX ${m.overflowX}px, times "${m.timeText}"`
    : `dead band ${worst}px (top ${m.top} / bottom ${m.bottom}, bar 48), ` +
      `control rows ${m.chromeRows} (1 expected), cover ${m.coverShare} of the ${m.areaHeight}px area, ` +
      `overflowX ${m.overflowX}px, times "${m.timeText}"`;
  return ok ? passCheck('audio-composition', msg, m) : failCheck('audio-composition', msg, m);
}

/**
 * Zen: every piece of chrome the protocol says must disappear has
 * display:none (sidebars, document header/action strip, status row, TinyMCE
 * menubar/formatting toolbar).
 */
export async function checkZenChromeHidden(page: Page): Promise<GeometryCheck> {
  const selectors = [
    '.sidebar-left',
    '.sidebar-right',
    '.editor-header',
    '.editor-status',
    '.tox-menubar',
    '.tox-toolbar-overlord',
  ];
  const displays = await page.evaluate((sels) => {
    const out: Record<string, string> = {};
    for (const sel of sels) {
      const el = document.querySelector(sel);
      out[sel] = el ? getComputedStyle(el).display : '<absent>';
    }
    return out;
  }, selectors);
  const violations = Object.entries(displays).filter(([, d]) => d !== 'none' && d !== '<absent>');
  const metrics = { displays };
  return violations.length === 0
    ? passCheck('zen-chrome-hidden', `zen hides all chrome: ${selectors.join(', ')}`, metrics)
    : failCheck(
        'zen-chrome-hidden',
        `zen chrome still visible: ${violations.map(([s, d]) => `${s}=${d}`).join(', ')}`,
        metrics
      );
}

/**
 * Zen: the writing surface must be horizontally centered — left and right
 * gutters balanced within 3px (editor centered via .editor-wrapper
 * align-items:center + symmetric padding; target max-width min(100%, 860px)).
 */
export async function checkZenGuttersBalanced(page: Page): Promise<GeometryCheck> {
  const surface = page.locator('app-markdown-editor').first();
  await surface.waitFor({ timeout: 30_000 });
  const rect = await surface.boundingBox();
  const vw = page.viewportSize()?.width ?? 0;
  if (!rect) return failCheck('zen-gutters-balanced', 'could not measure editor surface');
  const left = rect.x;
  const right = vw - (rect.x + rect.width);
  const delta = Math.abs(left - right);
  const ok = delta <= 3;
  const msg =
    `editor surface ${rect.width.toFixed(1)}px wide in ${vw}px viewport: left gutter ${left.toFixed(1)}px, ` +
    `right gutter ${right.toFixed(1)}px (delta ${delta.toFixed(1)}px, ${ok ? 'balanced' : 'UNBALANCED'})`;
  const metrics = { leftGutter: Math.round(left * 10) / 10, rightGutter: Math.round(right * 10) / 10, delta: Math.round(delta * 10) / 10, surfaceWidth: Math.round(rect.width) };
  return ok ? passCheck('zen-gutters-balanced', msg, metrics) : failCheck('zen-gutters-balanced', msg, metrics);
}

/** Zen: the studio host fills the viewport (position fixed, inset 0). */
export async function checkZenFillsViewport(page: Page): Promise<GeometryCheck> {
  const rect = await page.locator('.studio-layout').boundingBox();
  const vw = page.viewportSize()?.width ?? 0;
  const vh = page.viewportSize()?.height ?? 0;
  if (!rect) return failCheck('zen-fills-viewport', 'could not measure .studio-layout');
  const ok = Math.abs(rect.width - vw) <= 1 && Math.abs(rect.height - vh) <= 1;
  const msg = `studio layout ${rect.width.toFixed(0)}x${rect.height.toFixed(0)} vs viewport ${vw}x${vh} (${ok ? 'fills' : 'does not fill'})`;
  return ok ? passCheck('zen-fills-viewport', msg, { w: rect.width, h: rect.height }) : failCheck('zen-fills-viewport', msg, { w: rect.width, h: rect.height });
}

/**
 * Library toolbar: the progress filter must NOT be a combobox (<select>).
 * The only allowed toolbar select is the sort control. (The expert's full
 * contract — no toolbar progress surface at all — is enforced by
 * checkLibraryFilterContract when the progress-filter repair lands.)
 */
export async function checkLibraryNoProgressCombobox(page: Page): Promise<GeometryCheck> {
  const toolbar = page.locator('header.toolbar');
  await toolbar.waitFor({ timeout: 30_000 });
  const selects = await toolbar.locator('select').all();
  const found: string[] = [];
  for (const sel of selects) {
    const id = (await sel.getAttribute('id')) ?? (await sel.getAttribute('class')) ?? '<unnamed>';
    const options = await sel.locator('option').allTextContents();
    found.push(`${id} [${options.join(', ')}]`);
  }
  const progressSelect = found.find((f) => !/sort/i.test(f));
  if (progressSelect) {
    return failCheck('library-no-progress-combobox', `toolbar has a non-sort combobox: ${progressSelect}`, { found });
  }
  // If a progress filter control exists it must be a button-triggered menu, not a combobox.
  const triggerTag = await toolbar
    .locator('.filter-trigger')
    .evaluate((el) => (el ? el.tagName.toLowerCase() : '<absent>'))
    .catch(() => '<absent>');
  if (triggerTag !== '<absent>' && triggerTag !== 'button') {
    return failCheck('library-no-progress-combobox', `progress filter trigger is a <${triggerTag}>, expected a button menu`, { found, triggerTag });
  }
  return passCheck(
    'library-no-progress-combobox',
    `no progress combobox in toolbar (sort-only select${found.length ? `s: ${found.join('; ')}` : ''}; progress control is a ${triggerTag} menu)`,
    { found, triggerTag }
  );
}

/**
 * Reads the sidebar (or opened mobile drawer) status-filter labels. The
 * first .nav-group holds the progress filters; labels are visible whenever
 * the sidebar/drawer is expanded.
 */
export async function libraryFilterLabels(page: Page): Promise<string[]> {
  const labels = page.locator('.sidebar-panel nav.sidebar .nav-group').first().locator('span.label');
  await labels.first().waitFor({ timeout: 15_000 });
  return (await labels.allTextContents()).map((t) => t.trim());
}

/**
 * Six-filter contract check. Distinguishes three states honestly:
 *  - contract implemented and exact            -> pass
 *  - contract implemented but wrong            -> fail (real failure)
 *  - repair not merged yet (legacy UI: no
 *    'Not Started' item or legacy 'Reading'
 *    label still present)                      -> skipped (documented dependency)
 */
export async function checkLibraryFilterContract(labels: string[]): Promise<GeometryCheck> {
  const expected = [...LIBRARY_FILTER_CONTRACT];
  const preRepair = !labels.includes('Not Started') || labels.includes('Reading');
  if (preRepair) {
    return {
      id: 'library-six-sidebar-filters',
      pass: false,
      skipped: true,
      message:
        `SKIPPED: library filter contract not implemented in the merged build. ` +
        `Found ${labels.length} sidebar filters [${labels.join(', ')}]; contract requires exactly six ` +
        `[${expected.join(', ')}] with no toolbar progress surface (expert section 3). ` +
        `This check activates automatically when the progress-filter repair lands on main. ` +
        `See docs/visual-verification.md §Library.`,
      metrics: { found: labels, expected },
    };
  }
  const actual = [...labels].sort();
  const sortedExpected = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(sortedExpected)) {
    return failCheck(
      'library-six-sidebar-filters',
      `sidebar/drawer filters do not match the contract: found [${labels.join(', ')}], ` +
        `expected [${expected.join(', ')}]`,
      { found: labels, expected }
    );
  }
  return passCheck(
    'library-six-sidebar-filters',
    `sidebar/drawer exposes exactly the six contract filters: ${expected.join(', ')}`,
    { found: labels }
  );
}

// ---------------------------------------------------------------------------
// Real-library book discovery (reader surfaces only)
// ---------------------------------------------------------------------------

/** Finds a book with a file of the given kind at the real library origin. */
export async function findLibraryBook(
  kind: 'epub' | 'pdf' | 'audio'
): Promise<{ id: string; title: string; fileName: string } | null> {
  const res = await fetch(`${LIBRARY_URL}/api/books?pageSize=200`, {
    headers: { Accept: 'application/json' },
  });
  if (!res.ok) throw new Error(`GET ${LIBRARY_URL}/api/books -> ${res.status}: ${await res.text()}`);
  const data = (await res.json()) as { items?: Array<{ id: string; title: string; fileName?: string | null; hasFile?: boolean }> };
  const books = data.items ?? [];
  const extensions =
    kind === 'epub' ? ['.epub'] : kind === 'pdf' ? ['.pdf'] : ['.m4b', '.mp3', '.m4a', '.m4b'];
  const match = books.find((b) => {
    const name = (b.fileName ?? '').toLowerCase();
    return b.hasFile && extensions.some((ext) => name.endsWith(ext));
  });
  return match ? { id: match.id, title: match.title, fileName: match.fileName ?? '' } : null;
}

/** Minimal REST PUT for supported backend endpoints (writings content). */
export async function apiPut<T = unknown>(baseUrl: string, urlPath: string, body: unknown): Promise<T> {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${urlPath} -> ${res.status}: ${await res.text()}`);
  return (await res.json()) as T;
}

// ---------------------------------------------------------------------------
// Library toolbar stability (filter/sort cross-fade regression guard)
// ---------------------------------------------------------------------------

/**
 * The library toolbar must keep its static heading and centred search geometry,
 * and the result container must not change width when a filter change removes
 * the scrollbar. Both were real defects: a long dynamic title used to shove the
 * search field, and a short/empty result set dropped the styled 8px scrollbar
 * and shifted every column.
 *
 * The change under test is driven through the real search box: typing activates
 * a search chip and produces an empty result set, exercising both stability
 * guards at once.
 */
export async function checkLibraryToolbarStability(page: Page): Promise<GeometryCheck> {
  const search = page.locator('.search-input');
  const title = page.locator('#library-title');
  const scroller = page.locator('.library-right-side');
  const searchContainer = page.locator('.search-bar-container');
  const toolbarEl = page.locator('.toolbar');
  await search.waitFor({ timeout: 30_000 });

  const before = await search.boundingBox();
  const titleBefore = await title.boundingBox();
  const scrollBefore = await scroller.evaluate((el) => el.clientWidth);

  await search.fill('zzz-no-such-book-in-this-library');
  // 300ms input debounce + 200ms out-phase + 300ms in-phase + margin.
  await page.waitForTimeout(1500);

  const after = await search.boundingBox();
  const titleAfter = await title.boundingBox();
  const scrollAfter = await scroller.evaluate((el) => el.clientWidth);
  const containerBox = await searchContainer.boundingBox();
  const controlsBox = await page.locator('.toolbar-right').boundingBox();
  const toolbarBox = await toolbarEl.boundingBox();
  const toolbarPadding = await toolbarEl.evaluate((el) => {
    const style = getComputedStyle(el);
    return {
      paddingLeft: parseFloat(style.paddingLeft),
      paddingRight: parseFloat(style.paddingRight),
    };
  });
  if (
    !before ||
    !after ||
    !titleBefore ||
    !titleAfter ||
    !toolbarBox ||
    !containerBox ||
    !controlsBox
  ) {
    return failCheck(
      'library-toolbar-stability',
      'could not measure .search-input / #library-title',
    );
  }

  const dSearchX = Math.abs(after.x - before.x);
  const dSearchW = Math.abs(after.width - before.width);
  const dTitleX = Math.abs(titleAfter.x - titleBefore.x);
  const dTitleH = Math.abs(titleAfter.height - titleBefore.height);
  const dScrollW = Math.abs(scrollAfter - scrollBefore);
  const searchCenterX = after.x + after.width / 2;
  const toolbarCenterX =
    toolbarBox.x +
    toolbarPadding.paddingLeft +
    (toolbarBox.width - toolbarPadding.paddingLeft - toolbarPadding.paddingRight) / 2;
  const dCenterX = Math.abs(searchCenterX - toolbarCenterX);
  // Single row = the field shares a line with the control cluster (measured,
  // not read from the layout mode, so a regression in either mode is caught).
  // There the field must never reach the heading or the cluster (it once ran
  // under the sort dropdown), and it is centred unless the cluster is wide
  // enough to push it off centre, in which case it sits one column gap away.
  const singleRowCentered =
    containerBox.y < controlsBox.y + controlsBox.height &&
    containerBox.y + containerBox.height > controlsBox.y;
  const columnGap = await toolbarEl.evaluate((el) => parseFloat(getComputedStyle(el).columnGap));
  const gapToControls = controlsBox.x - (containerBox.x + containerBox.width);
  const gapToTitle = containerBox.x - (titleAfter.x + titleAfter.width);
  const clearOk = gapToControls >= columnGap - 1 && gapToTitle >= columnGap - 1;
  const centeringOk =
    !singleRowCentered ||
    (clearOk && (dCenterX <= 1 || Math.abs(gapToControls - columnGap) <= 1));
  const ok =
    dSearchX <= 1 && dSearchW <= 1 && dTitleX <= 1 && dTitleH <= 1 && dScrollW <= 1 && centeringOk;
  const metrics = {
    searchX: r1(before.x),
    searchXAfter: r1(after.x),
    dSearchX: r1(dSearchX),
    searchWidth: r1(before.width),
    dSearchW: r1(dSearchW),
    titleX: r1(titleBefore.x),
    dTitleX: r1(dTitleX),
    titleBoxHeight: r1(titleBefore.height),
    dTitleH: r1(dTitleH),
    scrollClientWidth: scrollBefore,
    dScrollW,
    searchCenterX: r1(searchCenterX),
    toolbarCenterX: r1(toolbarCenterX),
    dCenterX: r1(dCenterX),
    gapToControls: r1(gapToControls),
    gapToTitle: r1(gapToTitle),
  };
  const centeringMessage = singleRowCentered
    ? `, search centre ${r1(searchCenterX)} vs toolbar content centre ${r1(toolbarCenterX)} ` +
      `(Δ${r1(dCenterX)}px), gap to controls ${r1(gapToControls)}px, ` +
      `gap to title ${r1(gapToTitle)}px`
    : ', search centring skipped in stacked mode (full-width field)';
  const titleText = (await title.textContent())?.trim() ?? '';
  const message =
    `title "${titleText}": search x ${r1(before.x)} -> ${r1(after.x)} ` +
    `(Δ${r1(dSearchX)}px), search width Δ${r1(dSearchW)}px, title box x Δ${r1(dTitleX)}px, ` +
    `title box height Δ${r1(dTitleH)}px, results client width Δ${dScrollW}px${centeringMessage} ` +
    `(${ok ? 'stable' : 'SHIFTS'})`;
  return ok
    ? passCheck('library-toolbar-stability', message, metrics)
    : failCheck('library-toolbar-stability', message, metrics);
}

// ---------------------------------------------------------------------------
// Library — collections rail motion (fade, fold depth, fold continuity)
// ---------------------------------------------------------------------------

/**
 * Guards the rail's collapse motion plus the search field's icon inset - all
 * computed-CSS facts the unit tier cannot observe (it sees DOM and signals, not
 * transition timing or used grid tracks). Each assertion pins a specific
 * failure mode; the first, second and fourth were verified to FAIL against the
 * pre-fix values, and the third against a half-applied fix:
 *
 *  1. The section labels must FADE with the rest of the rail text. Left out of
 *     the opacity rules, they sat at opacity 1 for the whole 320ms fold and then
 *     vanished on the delayed `visibility: hidden` - a legible sliver right up
 *     to the last frame. Asserted as: at least one sample where the labels are
 *     already <= 0.1 opacity while the rail is still past half its travel.
 *  2. The fold must reach ZERO. A bare `1fr` track carries an `auto` minimum
 *     clamped to the child's content, so `0fr` settled at the label's line box
 *     (~16px) and left dead space in the rail. Asserted as: the settled track
 *     height is <= 0.5px.
 *  3. The fold must stay CONTINUOUS. This guards the FIX rather than the original
 *     bug: the pre-fix fold did interpolate, it simply stopped at ~16px. But
 *     declaring the min on only the collapsed state (`1fr` -> `minmax(0, 0fr)`)
 *     IS discrete and snaps (measured: 2 distinct heights) - the plausible way
 *     to half-apply the fold fix. Asserted as: more than three distinct heights.
 *  4. The search icon must clear the placeholder text: the glyph's box has to
 *     end before the text begins.
 */
export async function checkLibrarySidebarRail(page: Page): Promise<GeometryCheck> {
  const ID = 'library-sidebar-rail';
  await page.locator('.sidebar').first().waitFor({ timeout: 30_000 });

  // Mobile renders this same element as an off-canvas drawer: no rail width
  // motion and no fold, so the rail assertions do not apply.
  const drawer = await page.evaluate(() => window.innerWidth < 768);
  const railWidth = await page.evaluate(
    () => document.querySelector('app-sidebar-collections')?.getBoundingClientRect().width ?? 0,
  );
  if (drawer || railWidth === 0) {
    return {
      id: ID,
      pass: false,
      skipped: true,
      message: 'SKIPPED: mobile drawer — no rail width motion or fold to assert.',
    };
  }

  const reduced = await page.evaluate(() =>
    window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );

  // Start expanded so the collapse we measure is a real transition.
  const startsCollapsed = await page.evaluate(() =>
    document.querySelector('.sidebar')!.classList.contains('collapsed'),
  );
  if (startsCollapsed) {
    await page.locator('.toggle-btn').click();
    await page.waitForTimeout(600);
  }
  const expandedWidth = await page.evaluate(
    () => document.querySelector('app-sidebar-collections')!.getBoundingClientRect().width,
  );

  const samples = await page.evaluate(async () => {
    type Sample = { railW: number; labelOpacity: number; trackH: number };
    const out: Sample[] = [];
    const railEl = document.querySelector('app-sidebar-collections')!;
    const trackEl = document.querySelector('.collapsible')!;
    const labels = Array.from(document.querySelectorAll('.section-label'));
    const read = (): void => {
      out.push({
        railW: railEl.getBoundingClientRect().width,
        labelOpacity: labels.length
          ? Math.max(...labels.map((l) => parseFloat(getComputedStyle(l).opacity)))
          : 0,
        trackH: trackEl.getBoundingClientRect().height,
      });
    };
    document.querySelector<HTMLButtonElement>('.toggle-btn')!.click();
    await new Promise<void>((resolve) => {
      const t0 = performance.now();
      const iv = window.setInterval(() => {
        read();
        if (performance.now() - t0 > 700) {
          window.clearInterval(iv);
          resolve();
        }
      }, 16);
    });
    return out;
  });

  const last = samples[samples.length - 1];
  const halfTravel = last.railW + (expandedWidth - last.railW) / 2;
  const fadedWhileWide = samples.some((s) => s.labelOpacity <= 0.1 && s.railW > halfTravel);
  const settledTrack = Math.round(last.trackH * 10) / 10;
  const distinctTracks = new Set(samples.map((s) => Math.round(s.trackH))).size;

  // Measured in-page rather than with locator.boundingBox(): the icon component
  // and the <svg> inside it are two boxes for one glyph, so a tag-agnostic
  // locator can match twice (strict-mode violation), and boundingBox() also waits
  // on actionability. Reading the rendered geometry directly is unambiguous and
  // never waits — and the element it measures is the icon's HOST, which is what
  // the CSS around it positions.
  const icon = await page.evaluate(() => {
    const el = document.querySelector('.search-icon');
    const input = document.querySelector('.search-input');
    if (!el || !input) return null;
    const cs = getComputedStyle(input);
    const ir = el.getBoundingClientRect();
    const nr = input.getBoundingClientRect();
    return {
      iconRight: ir.right,
      textStart: nr.left + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft),
    };
  });
  if (!icon) {
    return failCheck(ID, 'could not measure .search-icon / .search-input');
  }
  const iconGap = r1(icon.textStart - icon.iconRight);

  const foldOk = settledTrack <= 0.5;
  const fadeOk = fadedWhileWide;
  const continuityOk = reduced || distinctTracks > 3;
  const iconOk = iconGap > 2;
  const ok = foldOk && fadeOk && continuityOk && iconOk;

  const metrics = {
    expandedWidth: r1(expandedWidth),
    collapsedWidth: r1(last.railW),
    halfTravel: r1(halfTravel),
    settledTrackHeight: settledTrack,
    distinctTrackHeights: distinctTracks,
    fadedWhileRailWide: fadedWhileWide,
    iconGap,
    reducedMotion: reduced,
    samples: samples.length,
  };
  const message =
    `rail ${r1(expandedWidth)} -> ${r1(last.railW)}px: section labels ` +
    `${fadeOk ? 'fade out before the rail closes' : 'STAY VISIBLE until the fold ends'}; ` +
    `settled label track ${settledTrack}px (${foldOk ? 'closes fully' : 'LEAVES DEAD SPACE'}); ` +
    `${distinctTracks} distinct fold heights (${continuityOk ? 'continuous' : 'SNAPS'}); ` +
    `search icon gap to text ${iconGap}px (${iconOk ? 'clear' : 'OVERLAPS'})`;
  return ok ? passCheck(ID, message, metrics) : failCheck(ID, message, metrics);
}

function r1(value: number): number {
  return Math.round(value * 10) / 10;
}

// ---------------------------------------------------------------------------
// Second Brain — pane and layout regression guards
// ---------------------------------------------------------------------------

/**
 * The Brain detail pane swaps content in place. This is the permanent visual
 * guard for the original flash complaint: a selected topic must not leave a
 * covering wait field, an `is-waiting` dim state, or an arrival animation
 * behind. The check is intentionally scoped to `.content-col`; the index is
 * allowed to retain its own shell animation.
 */
export async function checkBrainNoArrivalAnimation(page: Page): Promise<GeometryCheck> {
  const pane = page.locator('.content-col');
  await pane.locator('.topic-header').waitFor({ timeout: 30_000 });
  await pane.locator('.note-card').first().waitFor({ timeout: 30_000 });

  const state = await pane.evaluate((element) => {
    const animationNames: Record<string, string> = {};
    for (const selector of ['.topic-header', '.note-card']) {
      const target = element.querySelector(selector);
      animationNames[selector] = target ? getComputedStyle(target).animationName : '<absent>';
    }

    const waitElements = Array.from(element.querySelectorAll('.wait-field, .is-waiting')).map(
      (target) => `${target.tagName.toLowerCase()}.${target.className}`
    );
    if (element.classList.contains('is-waiting')) waitElements.push('.content-col.is-waiting');

    const runningAnimations = Array.from(document.getAnimations())
      .filter((animation) => animation.playState === 'running')
      .filter((animation) => {
        const effect = animation.effect as (AnimationEffect & { target?: Element | null }) | null;
        const target = effect?.target ?? null;
        return !!target && (target === element || element.contains(target));
      })
      .map((animation) => {
        const named = animation as Animation & { animationName?: string };
        return named.animationName ?? animation.constructor.name;
      });

    return { animationNames, waitElements, runningAnimations };
  });

  const animationViolations = Object.entries(state.animationNames).filter(([, name]) => name !== 'none');
  const ok =
    state.waitElements.length === 0 &&
    animationViolations.length === 0 &&
    state.runningAnimations.length === 0;
  const message = ok
    ? 'selected topic pane has no covering wait field, waiting class, or running arrival animation'
    : `Brain pane flash guard failed: wait elements [${state.waitElements.join(', ') || '—'}], ` +
      `animation names [${animationViolations.map(([selector, name]) => `${selector}=${name}`).join(', ') || '—'}], ` +
      `running animations [${state.runningAnimations.join(', ') || '—'}]`;

  return ok
    ? passCheck('brain-no-arrival-animation', message, state)
    : failCheck('brain-no-arrival-animation', message, state);
}

/**
 * Brain's desktop columns must fit their grid tracks; on mobile the same
 * check becomes a page/visible-descendant horizontal-overflow guard. The
 * measurements catch both a too-wide map stage and a detail card that causes
 * a hidden horizontal scrollbar or pushes the floating dock off-screen.
 */
export async function checkBrainLayoutOverflow(page: Page): Promise<GeometryCheck> {
  const metrics = await page.evaluate(() => {
    const layout = document.querySelector<HTMLElement>('.brain-layout');
    const selectors = ['.index-col', '.content-col'];
    const tracks = selectors.map((selector) => {
      const element = document.querySelector<HTMLElement>(selector);
      if (!element) {
        return { selector, visible: false, clientWidth: 0, scrollWidth: 0, left: 0, right: 0 };
      }
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return {
        selector,
        visible: style.display !== 'none',
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth,
        left: rect.left,
        right: rect.right,
      };
    });
    const visibleRightEdges = Array.from(document.querySelectorAll<HTMLElement>('.brain-layout *'))
      .filter((element) => {
        const style = getComputedStyle(element);
        return style.display !== 'none' && style.visibility !== 'hidden';
      })
      .map((element) => element.getBoundingClientRect().right)
      .filter((right) => Number.isFinite(right));
    const viewportWidth = window.innerWidth;
    const layoutRect = layout?.getBoundingClientRect();
    return {
      viewportWidth,
      documentScrollWidth: document.documentElement.scrollWidth,
      bodyScrollWidth: document.body.scrollWidth,
      layoutClientWidth: layout?.clientWidth ?? 0,
      layoutScrollWidth: layout?.scrollWidth ?? 0,
      layoutLeft: layoutRect?.left ?? 0,
      layoutRight: layoutRect?.right ?? viewportWidth,
      maxVisibleRight: visibleRightEdges.length ? Math.max(...visibleRightEdges) : 0,
      tracks,
    };
  });

  const layoutWidth = Math.max(0, metrics.layoutRight - metrics.layoutLeft);
  const trackViolations = metrics.tracks.filter(
    (track) =>
      track.visible &&
      (track.scrollWidth > track.clientWidth + 1 ||
        track.left < metrics.layoutLeft - 1 ||
        track.right > metrics.layoutRight + 1)
  );
  const pageOverflow =
    metrics.documentScrollWidth > metrics.viewportWidth + 1 ||
    metrics.bodyScrollWidth > metrics.viewportWidth + 1 ||
    metrics.maxVisibleRight > metrics.viewportWidth + 1;
  const ok = !trackViolations.length && !pageOverflow;
  const message = ok
    ? `Brain layout fits its tracks at ${metrics.viewportWidth}px with no horizontal overflow`
    : `Brain layout overflows at ${metrics.viewportWidth}px: ` +
      `${trackViolations.map((track) => `${track.selector} scroll ${track.scrollWidth}/${track.clientWidth}`).join(', ') || 'page extent exceeds viewport'}`;

  return ok
    ? passCheck('brain-layout-overflow', message, { ...metrics, layoutWidth, trackViolations, pageOverflow })
    : failCheck('brain-layout-overflow', message, { ...metrics, layoutWidth, trackViolations, pageOverflow });
}

/**
 * The map layout is hand-rolled SVG, so its evidence check verifies the DOM
 * graph rather than relying on pixels: every filtered topic has one node
 * and every rendered radius stays in the component's documented 14–34px
 * bounds.
 */
export async function checkBrainMapGeometry(page: Page): Promise<GeometryCheck> {
  const map = page.locator('.topic-map');
  await map.waitFor({ timeout: 30_000 });
  await page.waitForFunction(
    () => document.querySelector('.topic-map')?.getAttribute('aria-busy') === 'false',
    undefined,
    { timeout: 30_000 }
  );
  await map.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
  await page.waitForTimeout(600);

  // Node geometry comes from the live Sigma renderer: the map is a WebGL canvas,
  // so there are no per-node DOM elements to measure (the earlier SVG-based
  // version of this check read `circle.map-node-visual`, which no longer exists).
  const measured = await page.evaluate(() => {
    const badgeText = document.querySelector('.badge-count')?.textContent?.trim() ?? '';
    const topicCount = Number.parseInt(badgeText, 10);

    const globals = globalThis as unknown as {
      __nostosSigma?: {
        getDimensions(): { width: number; height: number };
        graphToViewport(p: { x: number; y: number }): { x: number; y: number };
      };
      __nostosGraph?: { order: number; forEachNode(cb: (id: string, attrs: { x: number; y: number; size: number }) => void): void };
    };
    const sigma = globals.__nostosSigma;
    const graph = globals.__nostosGraph;

    const sizes: number[] = [];
    const margins: number[] = [];
    if (sigma && graph) {
      const d = sigma.getDimensions();
      const xs: number[] = [];
      const ys: number[] = [];
      graph.forEachNode((_id, attrs) => {
        sizes.push(attrs.size);
        const p = sigma.graphToViewport({ x: attrs.x, y: attrs.y });
        xs.push(p.x);
        ys.push(p.y);
      });
      if (xs.length) {
        margins.push(
          Math.min(...xs),
          d.width - Math.max(...xs),
          Math.min(...ys),
          d.height - Math.max(...ys)
        );
      }
    }

    return {
      topicCount,
      nodeCount: graph ? graph.order : 0,
      accessibleTopicCount: document.querySelectorAll('.map-accessible-list li').length,
      sizes,
      onScreen: margins.every((v) => v >= -1),
    };
  });

  const sizesInBounds = measured.sizes.every(
    (size) => Number.isFinite(size) && size >= BRAIN_MAP_NODE_RADIUS.min && size <= BRAIN_MAP_NODE_RADIUS.max
  );
  const countMatches =
    Number.isFinite(measured.topicCount) &&
    measured.nodeCount === measured.topicCount &&
    measured.accessibleTopicCount === measured.topicCount;
  const ok = countMatches && sizesInBounds && measured.onScreen;
  const message = ok
    ? `map renders ${measured.nodeCount} node(s), matching the ${measured.topicCount}-topic index; sizes stay within ${BRAIN_MAP_NODE_RADIUS.min}–${BRAIN_MAP_NODE_RADIUS.max}px and all nodes are on screen`
    : `map geometry mismatch: ${measured.nodeCount} node(s), ${measured.topicCount} topic(s), ` +
      `${measured.accessibleTopicCount} accessible node(s), sizes [${measured.sizes.map((s) => s.toFixed(1)).join(', ')}], onScreen=${measured.onScreen}`;

  return ok
    ? passCheck('brain-map-geometry', message, { ...measured, sizeBounds: BRAIN_MAP_NODE_RADIUS })
    : failCheck('brain-map-geometry', message, { ...measured, sizeBounds: BRAIN_MAP_NODE_RADIUS });
}

// ---------------------------------------------------------------------------
// Book detail — cover echo (decorative cover-derived wash)
// ---------------------------------------------------------------------------

/** Finds a book that has cover art (the echo needs a real cover). */
export async function findLibraryCoverBook(): Promise<{ id: string; title: string } | null> {
  const res = await fetch(`${LIBRARY_URL}/api/books?pageSize=200`, {
    headers: { Accept: 'application/json' },
  });
  if (!res.ok) throw new Error(`GET ${LIBRARY_URL}/api/books -> ${res.status}: ${await res.text()}`);
  const data = (await res.json()) as {
    items?: Array<{ id: string; title: string; coverUrl?: string | null }>;
  };
  const match = (data.items ?? []).find((b) => !!b.coverUrl);
  return match ? { id: match.id, title: match.title } : null;
}

/**
 * Book detail hero criteria. Each one guards a defect that actually shipped:
 *
 *  1. FULL-BLEED BAND. The whole point of the layout is that the cover art fills
 *     the top of the page; the page previously rendered the cover in a narrow
 *     side column inside an 800px centred container, so most of a wide screen was
 *     empty. The band must span the scroll container's full width.
 *  2. ART ACTUALLY RENDERS. The art is carried by real `<img>` layers, never a
 *     `[style.background-image]` binding (the framework strips that and leaves an
 *     empty rectangle that still occupies space and still paints its scrim).
 *  3. NO COPY/COVER COLLISION. The sharp cover hangs over the band's fade via a
 *     negative margin and sits ABOVE the hero copy in the stacking order, so if
 *     the copy's clearance is ever reduced the cover silently paints over the
 *     author line. Assert the copy's last line ends above the cover's top edge.
 *  4. TITLE OWNS ITS PIXEL. Content paints above the decorative art layers.
 *  5. NO HORIZONTAL OVERFLOW (`.layout-content` is `overflow-y: auto`, which makes
 *     `overflow-x` compute to `auto` too, so a too-wide band scrolls the page).
 */
export async function checkBookDetailHero(page: Page): Promise<GeometryCheck> {
  const hero = page.locator('.book-hero').first();
  const title = page.locator('.book-title').first();
  await title.waitFor({ timeout: 30_000 });
  await hero.waitFor({ timeout: 30_000 });

  const m = await page.evaluate(() => {
    const q = (s: string) => document.querySelector(s);
    const rect = (e: Element | null) => (e ? e.getBoundingClientRect() : null);
    const round1 = (v: number) => Math.round(v * 10) / 10;
    const heroEl = q('.book-hero');
    const copy = q('.hero-copy');
    const coverEl = q('.cover-card');
    const cover = rect(coverEl);
    const scroller = q('.layout-content');
    // Last line of the hero copy, measured as the deepest content box so the
    // copy's bottom PADDING (which deliberately reaches the band's edge) is not
    // mistaken for the text's position.
    const lines = copy ? Array.from(copy.children).map((c) => rect(c)).filter(Boolean) : [];
    const copyContentBottom = lines.length
      ? Math.max(...lines.map((r) => (r as DOMRect).bottom))
      : null;
    const artImgs = Array.from(document.querySelectorAll('.hero-art img')) as HTMLImageElement[];
    const titleRect = rect(q('.book-title'));
    const hit = titleRect
      ? document.elementFromPoint(
          titleRect.left + titleRect.width / 2,
          titleRect.top + titleRect.height / 2
        )
      : null;
    return {
      hero: rect(heroEl),
      heroHeight: heroEl ? round1(heroEl.getBoundingClientRect().height) : null,
      scrollerWidth: scroller ? scroller.clientWidth : null,
      scrollerScrollWidth: scroller ? scroller.scrollWidth : null,
      copyContentBottom: copyContentBottom == null ? null : round1(copyContentBottom),
      coverTop: cover ? round1(cover.top) : null,
      artCount: artImgs.length,
      artLoaded: artImgs.filter((i) => i.complete && i.naturalWidth > 0).map((i) =>
        String(i.getAttribute('src')).split('/').pop()
      ),
      artSrcs: artImgs.map((i) => String(i.getAttribute('src'))),
      noArt: heroEl ? heroEl.classList.contains('no-art') : null,
      titleHit: hit ? String(hit.className || hit.tagName).slice(0, 48) : '<null>',
      docScroll: document.documentElement.scrollWidth,
      docClient: document.documentElement.clientWidth,
    };
  });

  const fullBleed = m.hero != null && m.scrollerWidth != null && Math.abs(m.hero.width - m.scrollerWidth) <= 2;
  const isHero = (m.heroHeight ?? 0) >= 260;
  const artRenders = m.artCount === 2 && m.artLoaded.length === 2 && m.noArt === false;
  // Positive gap => the copy ends above the cover's top edge.
  const clearance =
    m.copyContentBottom != null && m.coverTop != null ? m.coverTop - m.copyContentBottom : null;
  const noCollision = clearance != null && clearance >= 4;
  const contentOnTop = /book-title/.test(m.titleHit);
  const noOverflowX = m.docScroll <= m.docClient + 1;

  const ok = fullBleed && isHero && artRenders && noCollision && contentOnTop && noOverflowX;
  const metrics = { ...m, clearance };
  const msg =
    `band ${m.hero ? `${Math.round(m.hero.width)}x${m.heroHeight}px` : '?'} ` +
    `(${fullBleed ? 'full-bleed' : `NOT full-bleed vs ${m.scrollerWidth}px`}), ` +
    `${isHero ? 'hero-height' : 'TOO SHORT'}, ` +
    `art ${m.artLoaded.length}/${m.artCount} layers loaded ${artRenders ? '' : '(ART NOT RENDERING) '}` +
    `src=${m.artSrcs[0] ?? 'none'}, ` +
    `copy->cover clearance ${clearance}px ${noCollision ? '' : '(COPY/COVER COLLISION) '}` +
    `[copy ends ${m.copyContentBottom}, cover starts ${m.coverTop}], ` +
    `title owns its pixel ${contentOnTop ? 'yes' : `NO (${m.titleHit})`}, ` +
    `h-overflow ${noOverflowX ? 'none' : 'PRESENT'}`;
  return ok ? passCheck('book-detail-hero', msg, metrics) : failCheck('book-detail-hero', msg, metrics);
}
