/**
 * Add Book free-source discovery (#641) in a real engine.
 *
 * Public catalogues are not reachable from every environment, so the provider
 * API responses are fulfilled at the network layer with realistic payloads;
 * everything else (the Angular app, the real backend for the library shell)
 * is the actual product. The checks are layout properties jsdom cannot
 * measure: the loading surface and the results occupy the same box, the
 * results pane scrolls on its own, the footer's next action stays on screen,
 * narrow layouts do not scroll horizontally, and Back keeps the list's place.
 * Screenshots land in visual-evidence/ as the PR's before/after evidence.
 */
import { expect, test, type Page, type Route } from '@playwright/test';
import path from 'node:path';

import { loadFixture } from './fixture';

const EVIDENCE_DIR = path.join(__dirname, '..', 'visual-evidence');

// Capability names as the server reports them (lower-case flags): the waiting
// state names the sources that can search AND acquire the requested kind (#658).
const PROVIDERS = [
  { id: 'gutenberg', displayName: 'Project Gutenberg', capabilities: ['search', 'itemretrieval', 'ebookacquisition'] },
  { id: 'librivox', displayName: 'LibriVox', capabilities: ['search', 'itemretrieval', 'audiobookacquisition'] },
  { id: 'standardebooks', displayName: 'Standard Ebooks', capabilities: ['search', 'itemretrieval', 'ebookacquisition'] },
  { id: 'wikisource', displayName: 'Wikisource', capabilities: ['search', 'itemretrieval', 'ebookacquisition'] },
];

const TITLES = [
  'Pride and Prejudice', 'Emma', 'Persuasion', 'Sense and Sensibility', 'Mansfield Park',
  'Northanger Abbey', 'Lady Susan', 'Love and Freindship', 'The Watsons', 'Sanditon',
  'Juvenilia', 'Letters of Jane Austen', 'Jane Austen: Her Life and Letters', 'Memoir of Jane Austen',
];

function item(index: number) {
  const providerId = ['gutenberg', 'librivox', 'standardebooks'][index % 3];
  return {
    providerId,
    externalId: `x-${index}`,
    mediaKind: providerId === 'librivox' ? 'audiobook' : 'ebook',
    title: TITLES[index % TITLES.length],
    subtitle: null,
    author: 'Jane Austen',
    description: null,
    language: 'English',
    publisher: null,
    publishedDate: null,
    categories: index % 2 ? 'England -- Fiction' : 'Domestic fiction',
    narrator: providerId === 'librivox' ? 'Karen Savage' : null,
    duration: providerId === 'librivox' ? '11:35:04' : null,
    pageCount: null,
    assets: [],
    coverUrl: null,
    sourceUrl: null,
    rightsStatement: null,
    partCount: providerId === 'librivox' ? 61 : null,
  };
}

const LONG_DESCRIPTION = Array.from({ length: 9 }, () =>
  'It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife. '
  + 'However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so '
  + 'well fixed in the minds of the surrounding families, that he is considered the rightful property of some one or other of their daughters.',
).join(' ');

function detail(externalId: string) {
  const index = Number(externalId.split('-')[1]);
  const base = item(index);
  return {
    ...base,
    description: LONG_DESCRIPTION,
    sourceUrl: 'https://example.org/source',
    rightsStatement: 'Public domain in the USA.',
    assets: base.mediaKind === 'audiobook'
      ? [{ id: 'm4b', kind: 'audiobook', label: 'M4B audiobook', sourceFormat: 'audio/mp4', sizeBytes: 402_653_184, isPreferred: true }]
      : [
          { id: 'epub3', kind: 'ebook', label: 'EPUB3 (E-readers)', sourceFormat: 'application/epub+zip', sizeBytes: 812_000, isPreferred: true },
          { id: 'epub2', kind: 'ebook', label: 'EPUB (older E-readers)', sourceFormat: 'application/epub+zip', sizeBytes: 790_000, isPreferred: false },
          { id: 'pdf', kind: 'ebook', label: 'PDF', sourceFormat: 'application/pdf', sizeBytes: 2_400_000, isPreferred: false },
        ],
  };
}

interface Gate { release: () => void; }

async function mockProviders(page: Page, opts: { empty?: boolean } = {}): Promise<Gate> {
  let release!: () => void;
  const held = new Promise<void>((resolve) => (release = resolve));
  await page.route('**/api/providers', (route: Route) =>
    route.fulfill({ json: PROVIDERS }));
  await page.route('**/api/providers/search**', async (route: Route) => {
    await held;
    await route.fulfill({
      json: {
        items: opts.empty ? [] : TITLES.map((_, i) => item(i)),
        hasMore: !opts.empty,
        sources: [
          { providerId: 'gutenberg', displayName: 'Project Gutenberg', succeeded: true, notice: null, errorCode: null },
          { providerId: 'librivox', displayName: 'LibriVox', succeeded: true, notice: null, errorCode: null },
          { providerId: 'standardebooks', displayName: 'Standard Ebooks', succeeded: true, notice: null, errorCode: null },
          { providerId: 'wikisource', displayName: 'Wikisource', succeeded: false, notice: null, errorCode: 'provider_timeout' },
        ],
      },
    });
  });
  await page.route('**/api/providers/*/items/**', async (route: Route) => {
    const externalId = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop()!);
    await new Promise((resolve) => setTimeout(resolve, 150));
    await route.fulfill({ json: detail(externalId) });
  });
  return { release };
}

async function openDiscovery(page: Page, theme: 'light' | 'dark') {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/library`, { waitUntil: 'domcontentloaded' });
  await page.evaluate((t) => localStorage.setItem('nostos.theme', t), theme);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.library-add-button').first().click();
  await expect(page.locator('#source-query')).toBeVisible();
}

async function box(page: Page, selector: string) {
  const b = await page.locator(selector).boundingBox();
  if (!b) throw new Error(`no box for ${selector}`);
  return b;
}

async function snap(page: Page, name: string) {
  await page.screenshot({ path: path.join(EVIDENCE_DIR, `add-book-discovery-${name}.png`) });
}

export function addBookDiscoverySpecs(viewport: 'desktop' | 'mobile') {
  test.use({ serviceWorkers: 'block' });

  for (const theme of ['light', 'dark'] as const) {
    test(`${viewport} ${theme}: stable loading, spacious results, footer action`, async ({ page }) => {
      const gate = await mockProviders(page);
      await openDiscovery(page, theme);
      await snap(page, `${viewport}-${theme}-initial`);

      await page.locator('#source-query').fill('austen');
      await page.locator('#source-query').press('Enter');
      await expect(page.locator('[data-testid="source-loading"]')).toBeVisible();
      await expect(page.locator('.source-result')).toHaveCount(0);
      // The waiting state names every catalogue being asked (#658).
      await expect(page.locator('[data-testid="source-searching"] .nostos-badge')).toHaveCount(PROVIDERS.length);
      if (viewport === 'desktop') {
        // No dead void beside the waiting pane: the detail pane keeps a framed
        // placeholder of the same height (#658).
        const placeholder = page.locator('[data-testid="source-detail-placeholder"]');
        await expect(placeholder).toBeVisible();
        const loadingPane = await box(page, '[data-testid="source-results-pane"]');
        const placeholderBox = await placeholder.boundingBox();
        expect(Math.round(placeholderBox!.height)).toBe(Math.round(loadingPane.height));
        expect(await placeholder.evaluate((el) => getComputedStyle(el).borderTopStyle)).toBe('dashed');
      }
      const loadingBox = await box(page, '.source-workspace');
      await snap(page, `${viewport}-${theme}-loading`);

      gate.release();
      await expect(page.locator('.source-result')).toHaveCount(TITLES.length);
      const resultsBox = await box(page, '.source-workspace');
      expect(Math.round(resultsBox.height), 'loading and results reserve the same box').toBe(Math.round(loadingBox.height));
      expect(resultsBox.height, 'far more room than the old 15rem (240px) picker').toBeGreaterThan(240);

      const pane = page.locator('[data-testid="source-results-pane"]');
      const scrolls = await pane.evaluate((el) => el.scrollHeight > el.clientHeight);
      expect(scrolls, 'a long list scrolls inside its pane').toBe(true);
      await expect(page.locator('[data-testid="source-partial"]')).toContainText('Wikisource · timed out');
      const footer = page.locator('[data-testid="source-use-book"]');
      await expect(footer).toHaveText(/Select a book/);
      await expect(footer).toBeDisabled();
      await snap(page, `${viewport}-${theme}-results`);

      // Pick a result far down the list, as a reader hunting would.
      await pane.evaluate((el) => (el.scrollTop = el.scrollHeight));
      const scrolledTo = await pane.evaluate((el) => el.scrollTop);
      const order = await page.locator('.source-result-title').allTextContents();
      await page.locator('.source-result').nth(10).click();
      await expect(footer).toHaveText(/Use this book/);
      await expect(footer).toBeEnabled();

      const viewportSize = page.viewportSize()!;
      const footerBox = await footer.boundingBox();
      expect(footerBox!.y + footerBox!.height, 'the next action stays on screen').toBeLessThanOrEqual(viewportSize.height);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no horizontal page scroll').toBe(true);
      await snap(page, `${viewport}-${theme}-selected`);

      if (viewport === 'mobile') {
        await expect(pane).toBeHidden();
        await page.locator('[data-testid="source-back"]').click();
        await expect(pane).toBeVisible();
        expect(await pane.evaluate((el) => el.scrollTop), 'Back keeps the list position').toBeCloseTo(scrolledTo, -1);
        await snap(page, `${viewport}-${theme}-back`);
      } else {
        await expect(pane).toBeVisible();
        await expect(page.locator('[data-testid="source-detail-pane"]')).toContainText('It is a truth universally acknowledged');
      }
      expect(await page.locator('.source-result-title').allTextContents(), 'selection never reorders results').toEqual(order);

      await footer.click();
      await expect(page.locator('#source-query')).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Import Book' })).toBeVisible();
    });
  }

  test(`${viewport}: no results state`, async ({ page }) => {
    const gate = await mockProviders(page, { empty: true });
    gate.release();
    await openDiscovery(page, 'light');
    await page.locator('#source-query').fill('zzzz');
    await page.locator('#source-query').press('Enter');
    await expect(page.getByText('No matching books were found')).toBeVisible();
    await snap(page, `${viewport}-light-no-results`);
  });
}
