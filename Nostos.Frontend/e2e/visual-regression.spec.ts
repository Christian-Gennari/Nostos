/**
 * Visual-regression evidence harness — fixture-served UI and optional real-library reader captures.
 *
 * Reusable, parameterized capture(surface, viewport, state) that turns the
 * mandatory visual-verification protocol (expert section 4) into a
 * mechanical, committable artifact: each capture produces a PNG + a geometry
 * JSON report under e2e/visual-evidence/, and the automated geometry checks
 * from the protocol run as real assertions:
 *
 *   - EPUB iframe foreground/background match the light normalization
 *     constants (and the shell surface matches too — no pale rim);
 *   - PDF scrollport bottom clears the shell toolbar at the final page;
 *   - Zen gutters balanced + all zen chrome display:none;
 *   - Library toolbar has no progress combobox; sidebar/drawer exposes the
 *     six contract filters.
 *
 * This harness currently initializes the app in light theme. Light and dark
 * are both supported app themes; the EPUB assertion below checks its
 * light-specific normalization and does not describe the app's only rendering.
 *
 * Surfaces that need data the isolated fixture cannot provide are SKIPPED
 * with a clear, documented message — never faked:
 *   - EPUB/PDF reader surfaces need real book files. No EPUB/PDF test assets
 *     exist in-repo, so by default they skip; set VISUAL_QA_LIBRARY_URL to a
 *     real library instance (serving the build under test) to capture them.
 *   - The library six-filter sidebar contract needs the progress-filter
 *     repair (expert section 3) merged into main. Until it lands the check
 *     reports a documented skip; it activates automatically once merged.
 *
 * The spec runs in the desktop project (config testIgnore covers mobile.*).
 * Mobile captures open their own 390x844 context with touch emulation, so
 * artifact pixels are exactly 1440x900 / 390x844 per the protocol.
 */
import { expect, test } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import {
  artifactPath,
  capturePng,
  checkAudioComposition,
  checkEpubIframeLight,
  checkLibraryFilterContract,
  checkLibraryToolbarStability,
  checkLibraryNoProgressCombobox,
  checkLibrarySidebarRail,
  checkPdfFinalPageClearance,
  checkZenChromeHidden,
  checkZenFillsViewport,
  checkZenGuttersBalanced,
  DESKTOP_VIEWPORT,
  findLibraryBook,
  libraryFilterLabels,
  LIBRARY_URL,
  LANDSCAPE_VIEWPORT,
  MOBILE_VIEWPORT,
  newCapturePage,
  READER_SKIP_REASON,
  writeGeometryReport,
  apiPut,
  checkBrainLayoutOverflow,
  checkBrainMapGeometry,
  checkBrainNoArrivalAnimation,
  type CaptureMeta,
  type GeometryCheck,
} from './support/visual-capture';

test.describe.configure({ mode: 'serial' });

const RUN = newRunId();
const SEED_CLIENT = 'visual-qa';
const DOC_TITLE = 'Visual QA Draft';

let fixture: ReturnType<typeof loadFixture>;

function meta(
  name: string,
  surface: CaptureMeta['surface'],
  viewport: CaptureMeta['viewport'],
  state: string
): CaptureMeta {
  return { name, surface, viewport, state };
}

async function expectChecks(name: string, checks: GeometryCheck[], m: CaptureMeta): Promise<void> {
  await writeGeometryReport(name, checks, m);
  for (const check of checks) {
    // Skipped checks are documented dependencies (see geometry JSON + docs);
    // every non-skipped check must pass for the capture to be green.
    if (!check.skipped) {
      expect(check.pass, check.message).toBe(true);
    }
  }
}

// ---------------------------------------------------------------------------
// Writing Studio — fixture-served (no book assets needed)
// ---------------------------------------------------------------------------

test.describe('visual matrix — Writing Studio (fixture-served)', () => {
  let docId: string | null = null;

  test('studio-empty-desktop', async ({ browser }) => {
    fixture = loadFixture();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
      await page.locator('.editor-pane .empty-state').waitFor({ timeout: 30_000 });
      await expect(page.locator('.editor-pane .empty-state h2')).toHaveText('Open a document to begin writing');
      await expect(page.locator('.editor-pane .empty-subtext')).toHaveText(
        'Choose one from Files, or start a new document.',
      );
      const png = await capturePng(page, 'studio-empty-desktop');
      await writeGeometryReport(
        'studio-empty-desktop',
        [
          {
            id: 'studio-empty-state',
            pass: true,
            message: 'empty studio invites the user to open a document or start a new one',
            metrics: { png },
          },
        ],
        meta('studio-empty-desktop', 'studio', DESKTOP_VIEWPORT, 'no-document')
      );
    } finally {
      await context.close();
    }
  });

  test('studio-document-desktop', async ({ browser }) => {
    // Seed one document through the supported REST surface, then open it.
    const created = await apiPost<{ id: string }>(fixture.baseUrl, '/api/writings', {
      name: DOC_TITLE,
      type: 'Document',
      parentId: null,
    });
    docId = created.id;
    await apiPut(fixture.baseUrl, `/api/writings/${docId}`, {
      name: DOC_TITLE,
      content:
        '# Visual QA Draft\n\nA paragraph of representative prose so the editor has visible ' +
        'content in the evidence capture.\n\n## Section\n\nMore prose, a few sentences long, ' +
        'to give the writing surface a realistic height in the screenshot.',
    });

    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
      await page
        .locator('.file-list .tree-row', { hasText: DOC_TITLE })
        .waitFor({ timeout: 30_000 });
      await page.locator('.file-list .tree-row', { hasText: DOC_TITLE }).click();
      await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
      await page.waitForTimeout(800); // let TinyMCE layout settle
      await expect(page.locator('.editor-header .zen-toggle')).toBeVisible();

      const png = await capturePng(page, 'studio-document-desktop');
      await writeGeometryReport(
        'studio-document-desktop',
        [
          {
            id: 'studio-document-open',
            pass: true,
            message: `document '${DOC_TITLE}' open in the editor with normal chrome visible`,
            metrics: { png },
          },
        ],
        meta('studio-document-desktop', 'studio', DESKTOP_VIEWPORT, 'document-open')
      );
    } finally {
      await context.close();
    }
  });

  test('studio-zen-desktop', async ({ browser }) => {
    expect(docId, 'studio-document-desktop must run first (serial)').toBeTruthy();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
      await page.locator('.file-list .tree-row', { hasText: DOC_TITLE }).waitFor({ timeout: 30_000 });
      await page.locator('.file-list .tree-row', { hasText: DOC_TITLE }).click();
      await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
      await page.locator('.editor-header .zen-toggle').click();
      await page.locator('.zen-exit').waitFor({ timeout: 15_000 });
      await page.waitForTimeout(400);

      const checks = [
        await checkZenFillsViewport(page),
        await checkZenChromeHidden(page),
        await checkZenGuttersBalanced(page),
      ];
      await expectChecks('studio-zen-desktop', checks, meta('studio-zen-desktop', 'studio', DESKTOP_VIEWPORT, 'zen'));
      await capturePng(page, 'studio-zen-desktop');
    } finally {
      await context.close();
    }
  });

  test('studio-zen-mobile', async ({ browser }) => {
    expect(docId, 'studio-document-desktop must run first (serial)').toBeTruthy();
    const { context, page } = await newCapturePage(browser, MOBILE_VIEWPORT, true);
    try {
      await page.goto(`${fixture.baseUrl}/studio`, { waitUntil: 'domcontentloaded' });
      // On mobile the file sidebar starts closed, so it has to be opened
      // through the UI before any tree row is reachable. Wait for the app to
      // render first: a one-shot isVisible() probe races Angular's bootstrap,
      // silently skips the click, and leaves the row off-screen (the drawer is
      // translated -100% until it carries `.open`).
      const fileRow = page.locator('.file-list .tree-row', { hasText: DOC_TITLE });
      await fileRow.waitFor({ timeout: 30_000 });
      const fileSidebar = page.locator('.sidebar-left');
      if (!(await fileSidebar.evaluate((el) => el.classList.contains('open')))) {
        // The persistent editor-header control opens the default-closed Files drawer.
        await page.locator('.files-toggle').click();
      }
      await expect(fileSidebar).toHaveClass(/\bopen\b/, { timeout: 15_000 });
      await fileRow.click();
      await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
      // Selecting a document closes the mobile sidebar; the zen toggle lives in
      // the (now visible) editor header.
      await page.locator('.editor-header .zen-toggle').click();
      await page.locator('.zen-exit').waitFor({ timeout: 15_000 });
      await page.waitForTimeout(400);

      const checks = [
        await checkZenFillsViewport(page),
        await checkZenChromeHidden(page),
        await checkZenGuttersBalanced(page),
      ];
      await expectChecks('studio-zen-mobile', checks, meta('studio-zen-mobile', 'studio', MOBILE_VIEWPORT, 'zen'));
      await capturePng(page, 'studio-zen-mobile');
    } finally {
      await context.close();
    }
  });
});

// ---------------------------------------------------------------------------
// Library — fixture-served
// ---------------------------------------------------------------------------

test.describe('visual matrix — Library (fixture-served)', () => {
  test.beforeAll(async () => {
    fixture = loadFixture();
    // Populate the library through supported REST so the filters page is not empty.
    const books: Array<{ type: string; title: string; author?: string; categories?: string }> = [
      { type: 'physical', title: 'Visual QA: Meditations', author: 'Marcus Aurelius', categories: 'philosophy' },
      { type: 'physical', title: 'Visual QA: Candide', author: 'Voltaire', categories: 'literature' },
      { type: 'physical', title: 'Visual QA: Poetics', author: 'Aristotle', categories: 'philosophy' },
      { type: 'physical', title: 'Visual QA: Symposium', author: 'Plato', categories: 'philosophy' },
    ];
    for (const book of books) {
      await apiPost(fixture.baseUrl, '/api/books', { ...book });
    }
  });

  test('library-filters-desktop', async ({ browser }) => {
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
      await page.locator('header.toolbar').waitFor({ timeout: 30_000 });
      await page.locator('.sidebar-panel nav.sidebar .nav-group .nav-item').first().waitFor({ timeout: 15_000 });
      await page.waitForTimeout(500);

      // Evidence first: the PNG is produced even when a documented dependency
      // below is not merged yet.
      await capturePng(page, 'library-filters-desktop');

      const checks: GeometryCheck[] = [];
      checks.push(await checkLibraryNoProgressCombobox(page));
      checks.push(await checkLibraryFilterContract(await libraryFilterLabels(page)));
      checks.push(await checkLibraryToolbarStability(page));
      checks.push(await checkLibrarySidebarRail(page));
      await expectChecks('library-filters-desktop', checks, meta('library-filters-desktop', 'library', DESKTOP_VIEWPORT, 'filters'));
    } finally {
      await context.close();
    }
  });

  test('library-filters-mobile', async ({ browser }) => {
    const { context, page } = await newCapturePage(browser, MOBILE_VIEWPORT, true);
    try {
      await page.goto(`${fixture.baseUrl}/library`, { waitUntil: 'domcontentloaded' });
      await page.locator('header.toolbar').waitFor({ timeout: 30_000 });
      // Open the sidebar drawer (off-canvas on mobile).
      await page.locator('.floating-toggle').click();
      await page.locator('.mobile-backdrop').waitFor({ timeout: 15_000 });
      await page.locator('.sidebar-panel nav.sidebar .nav-group .nav-item').first().waitFor({ timeout: 15_000 });
      await page.waitForTimeout(500);

      await capturePng(page, 'library-filters-mobile');

      const checks: GeometryCheck[] = [];
      checks.push(await checkLibraryNoProgressCombobox(page));
      checks.push(await checkLibraryFilterContract(await libraryFilterLabels(page)));

      // Close the drawer so the search box is actionable, then assert the mobile
      // toolbar/results column does not move when the search shrinks the result
      // set and the page scrollbar disappears.
      // (Via a dispatched click: the toggle hides while open, and the backdrop's
      // centre sits under the drawer, so neither is a normal click target.)
      await page.locator('.mobile-backdrop').dispatchEvent('click');
      await page.locator('.mobile-backdrop').waitFor({ state: 'detached', timeout: 15_000 });
      checks.push(await checkLibraryToolbarStability(page));

      await expectChecks('library-filters-mobile', checks, meta('library-filters-mobile', 'library', MOBILE_VIEWPORT, 'drawer-open'));
    } finally {
      await context.close();
    }
  });
});

// ---------------------------------------------------------------------------
// Second Brain — fixture-served
// ---------------------------------------------------------------------------

interface BrainSeed {
  bookId: string;
  topicNames: string[];
}

let brainSeed: BrainSeed | null = null;

async function seedBrainFixture(): Promise<BrainSeed> {
  if (brainSeed) return brainSeed;

  fixture ??= loadFixture();
  const suffix = RUN;
  const created = await apiPost<{ id: string }>(fixture.baseUrl, '/api/books', {
    type: 'physical',
    title: `Visual QA Brain ${suffix}`,
    author: 'Nostos Visual QA',
    categories: 'visual-qa',
  });
  const notes = [
    'A first reflection on [[Attention]] and [[Memory]].',
    'A second reflection on [[Attention]] and [[Practice]].',
    'A third reflection where [[Memory]] meets [[Practice]].',
  ];
  for (const content of notes) {
    await apiPost(fixture.baseUrl, `/api/books/${created.id}/notes`, { content });
  }

  brainSeed = { bookId: created.id, topicNames: ['Attention', 'Memory', 'Practice'] };
  return brainSeed;
}

test.describe('visual matrix — Second Brain (fixture-served)', () => {
  test('brain-empty-desktop', async ({ browser }) => {
    // This runs before Brain seeding in the serial matrix, so the empty state
    // proves the real fresh fixture rather than a client-side fake.
    fixture = loadFixture();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      const empty = page.locator('.empty-index-state');
      await empty.waitFor({ timeout: 30_000 });
      await expect(empty).toContainText('[[Topic Name]]');
      await expect(page.locator('.index-item')).toHaveCount(0);
      await page.waitForTimeout(400); // let the allowed index shell entrance settle

      const checks: GeometryCheck[] = [
        {
          id: 'brain-empty-state',
          pass: await empty.isVisible(),
          message: 'fresh fixture shows the Second Brain [[ ]] empty state',
        },
        await checkBrainLayoutOverflow(page),
      ];
      const png = await capturePng(page, 'brain-empty-desktop');
      await expectChecks(
        'brain-empty-desktop',
        checks,
        meta('brain-empty-desktop', 'brain', DESKTOP_VIEWPORT, 'empty-index')
      );
      expect(artifactPath('brain-empty-desktop', 'png')).toBe(png);
    } finally {
      await context.close();
    }
  });

  test('brain-index-desktop', async ({ browser }) => {
    await seedBrainFixture();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
      await expect(page.locator('.index-item')).toHaveCount(3);
      await expect(page.locator('.index-stats')).toContainText('3 topics');
      await page.waitForTimeout(250);

      const checks: GeometryCheck[] = [await checkBrainLayoutOverflow(page)];
      const png = await capturePng(page, 'brain-index-desktop');
      await expectChecks(
        'brain-index-desktop',
        checks,
        meta('brain-index-desktop', 'brain', DESKTOP_VIEWPORT, 'index-list')
      );
      expect(artifactPath('brain-index-desktop', 'png')).toBe(png);
    } finally {
      await context.close();
    }
  });

  test('brain-index-mobile', async ({ browser }) => {
    expect(brainSeed, 'brain-index-desktop must seed the fixture first').not.toBeNull();
    const { context, page } = await newCapturePage(browser, MOBILE_VIEWPORT, true);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
      await expect(page.locator('.index-col')).toBeVisible();
      await expect(page.locator('.content-col')).toBeHidden();
      await page.waitForTimeout(250);

      const checks: GeometryCheck[] = [await checkBrainLayoutOverflow(page)];
      const png = await capturePng(page, 'brain-index-mobile');
      await expectChecks(
        'brain-index-mobile',
        checks,
        meta('brain-index-mobile', 'brain', MOBILE_VIEWPORT, 'index-list')
      );
      expect(artifactPath('brain-index-mobile', 'png')).toBe(png);
    } finally {
      await context.close();
    }
  });

  test('brain-topic-desktop', async ({ browser }) => {
    expect(brainSeed, 'brain-index-desktop must seed the fixture first').not.toBeNull();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      const firstRow = page.locator('.index-item').first();
      await firstRow.waitFor({ timeout: 30_000 });
      // Hover is part of the supported interaction: it prefetches the detail,
      // so the captured click exercises the no-flash, cache-hit path.
      await firstRow.hover();
      await page.waitForTimeout(250);
      await firstRow.click();
      await page.locator('.topic-header').waitFor({ timeout: 30_000 });
      await page.locator('.note-card').first().waitFor({ timeout: 30_000 });
      await page.waitForTimeout(250);

      const paneCheck = await checkBrainNoArrivalAnimation(page);
      const checks: GeometryCheck[] = [paneCheck, await checkBrainLayoutOverflow(page)];
      const png = await capturePng(page, 'brain-topic-desktop');

      // Exercise the guard's negative path in-browser, then remove the probe
      // before writing the report. This is a cheap sanity check that a future
      // pane-in animation would turn this gate red.
      const probeStyle = await page.addStyleTag({
        content:
          '@keyframes brain-pane-regression-probe { from { opacity: .9; } to { opacity: 1; } }' +
          '.content-col .topic-header { animation: brain-pane-regression-probe 1s linear !important; }',
      });
      const tampered = await checkBrainNoArrivalAnimation(page);
      await probeStyle.evaluate((element) => element.remove());
      await page.waitForTimeout(50);
      const restored = await checkBrainNoArrivalAnimation(page);
      expect(tampered.pass, 'flash guard must reject a temporary pane animation').toBe(false);
      expect(restored.pass, 'flash guard must pass after the temporary animation is removed').toBe(true);
      checks.push({
        id: 'brain-flash-guard-sanity',
        pass: !tampered.pass && restored.pass,
        message: 'flash guard rejected a temporary pane animation and passed after it was removed',
        metrics: {
          injectedCheck: tampered.message,
          restoredCheck: restored.message,
        },
      });

      await expectChecks(
        'brain-topic-desktop',
        checks,
        meta('brain-topic-desktop', 'brain', DESKTOP_VIEWPORT, 'topic-selected-notes')
      );
      expect(artifactPath('brain-topic-desktop', 'png')).toBe(png);
    } finally {
      await context.close();
    }
  });

  test('brain-map-desktop', async ({ browser }) => {
    expect(brainSeed, 'brain-index-desktop must seed the fixture first').not.toBeNull();
    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
      await page.getByRole('button', { name: 'Map view' }).click();
      await page.locator('.topic-map').waitFor({ timeout: 30_000 });
      // Wait on the Sigma renderer, not the pre-rewrite SVG node elements
      // (`.map-node` no longer exists and made this wait impossible to satisfy).
      await page.locator('.sigma-container canvas').first().waitFor({ timeout: 30_000 });
      await page.waitForFunction(
        () => document.querySelector('.topic-map')?.getAttribute('aria-busy') === 'false',
        undefined,
        { timeout: 30_000 }
      );

      const checks: GeometryCheck[] = [
        await checkBrainMapGeometry(page),
        await checkBrainLayoutOverflow(page),
      ];
      const png = await capturePng(page, 'brain-map-desktop');
      await expectChecks(
        'brain-map-desktop',
        checks,
        meta('brain-map-desktop', 'brain', DESKTOP_VIEWPORT, 'map')
      );
      expect(artifactPath('brain-map-desktop', 'png')).toBe(png);
    } finally {
      await context.close();
    }
  });
});

// ---------------------------------------------------------------------------
// Reader surfaces — EPUB/PDF (real library only; skipped otherwise)
// ---------------------------------------------------------------------------

interface ReaderCase {
  name: string;
  kind: 'epub' | 'pdf' | 'audio';
  viewport: { width: number; height: number };
  mobile: boolean;
  state: string;
}

const READER_MATRIX: ReaderCase[] = [
  { name: 'epub-light-desktop', kind: 'epub', viewport: DESKTOP_VIEWPORT, mobile: false, state: 'fixed-light' },
  { name: 'epub-light-mobile', kind: 'epub', viewport: MOBILE_VIEWPORT, mobile: true, state: 'fixed-light' },
  { name: 'pdf-light-desktop', kind: 'pdf', viewport: DESKTOP_VIEWPORT, mobile: false, state: 'final-page-bottom' },
  { name: 'pdf-light-mobile-bottom', kind: 'pdf', viewport: MOBILE_VIEWPORT, mobile: true, state: 'final-page-bottom' },
  { name: 'audio-light-desktop', kind: 'audio', viewport: DESKTOP_VIEWPORT, mobile: false, state: 'composition' },
  { name: 'audio-light-mobile', kind: 'audio', viewport: MOBILE_VIEWPORT, mobile: true, state: 'composition' },
  { name: 'audio-light-landscape', kind: 'audio', viewport: LANDSCAPE_VIEWPORT, mobile: true, state: 'phone-landscape' },
];

test.describe('visual matrix — Reader surfaces (real library)', () => {
  for (const tc of READER_MATRIX) {
    test(`${tc.name}`, async ({ browser }) => {
      test.skip(!LIBRARY_URL, READER_SKIP_REASON);

      const book = await findLibraryBook(tc.kind);
      test.skip(
        !book,
        `no ${tc.kind} book with a file found at VISUAL_QA_LIBRARY_URL (${LIBRARY_URL}) — ` +
          `add one to the real library to capture '${tc.name}' (see docs/visual-verification.md §Reader surfaces)`
      );

      const { context, page } = await newCapturePage(browser, tc.viewport, tc.mobile);
      try {
        await page.goto(`${LIBRARY_URL}/read/${book!.id}`, { waitUntil: 'domcontentloaded' });

        if (tc.kind === 'epub') {
          await page.locator('#epub-viewer iframe').waitFor({ state: 'attached', timeout: 45_000 });
          await page
            .frameLocator('#epub-viewer iframe')
            .locator('body')
            .first()
            .waitFor({ timeout: 45_000 });
        } else if (tc.kind === 'pdf') {
          await page.locator('#viewerContainer canvas').first().waitFor({ timeout: 60_000 });
        } else {
          await page.locator('.audio-container').waitFor({ timeout: 60_000 });
        }

        // No theme interaction: the app ships exactly one light rendering and
        // the reader checks below cover light EPUB normalization and PDF clearance.
        const checks: GeometryCheck[] = [];
        if (tc.kind === 'epub') {
          checks.push(await checkEpubIframeLight(page));
          // Highlight note: the app has no programmatic highlight-placement API —
          // highlights require real user selection inside the book, which the
          // harness cannot synthesize. Documented in docs/visual-verification.md.
        } else if (tc.kind === 'pdf') {
          checks.push(await checkPdfFinalPageClearance(page));
        } else {
          checks.push(await checkAudioComposition(page));
        }

        await expectChecks(tc.name, checks, meta(tc.name, tc.kind, tc.viewport, tc.state));
        const png = await capturePng(page, tc.name);
        expect(artifactPath(tc.name, 'png')).toBe(png); // evidence path sanity
      } finally {
        await context.close();
      }
    });
  }
});
