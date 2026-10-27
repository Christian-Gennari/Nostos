import { expect, test, type Browser, type Page } from '@playwright/test';

import { apiGet, apiPost, loadFixture } from './support/fixture';

/**
 * Issue #1220 on the real backend: highlights imported from a Kindle arrive
 * in a starter draft, quoted with their sources, from the import result and
 * from the empty Studio's one-time offer.
 */

const EVIDENCE = 'e2e/visual-evidence/starter-draft';

const MEDITATIONS = 'Meditations';
const ESSAYS = 'Essays';

const PASSAGES = [
  {
    book: MEDITATIONS,
    author: 'Marcus Aurelius',
    text: 'You have power over your mind, not outside events. Realize this, and you will find strength.',
  },
  {
    book: ESSAYS,
    author: 'Michel de Montaigne',
    text: 'The greatest thing in the world is to know how to belong to oneself.',
  },
  {
    book: MEDITATIONS,
    author: 'Marcus Aurelius',
    text: 'The happiness of your life depends upon the quality of your thoughts.',
  },
];

function clippings(): string {
  return PASSAGES.map(
    (passage, index) =>
      `${passage.book} (${passage.author})\n` +
      `- Your Highlight on page ${12 + index} | Location ${180 + index * 10}-${182 + index * 10} | Added on Monday, 3 March 2025 10:0${index}:00\n\n` +
      `${passage.text}\n==========`,
  ).join('\n');
}

async function freshPage(browser: Browser, baseURL: string, viewport = { width: 1280, height: 800 }, dark = false): Promise<Page> {
  const context = await browser.newContext({ baseURL, viewport, colorScheme: dark ? 'dark' : 'light', serviceWorkers: 'block' });
  return context.newPage();
}

test('imported highlights start a draft with their sources (issue #1220)', async ({ browser }) => {
  const { baseUrl } = loadFixture();

  const writings = await apiGet<unknown[]>(baseUrl, '/api/writings');
  test.skip(writings.length > 0, 'The empty-Studio offer needs a library with no writings; run this spec on its own fixture.');

  for (const title of [MEDITATIONS, ESSAYS]) {
    const author = PASSAGES.find((passage) => passage.book === title)!.author;
    await apiPost(baseUrl, '/api/books', { type: 'physical', title, author });
  }

  // Import My Clippings.txt from Settings.
  const importer = await freshPage(browser, baseUrl);
  await importer.goto('/settings?section=library');
  await importer.getByTestId('highlight-import-action').click();
  await importer.getByTestId('highlight-import-input').setInputFiles({
    name: 'My Clippings.txt',
    mimeType: 'text/plain',
    buffer: Buffer.from(clippings(), 'utf8'),
  });
  await importer.getByTestId('highlight-import-commit').click();
  const start = importer.getByTestId('highlight-start-draft');
  await expect(start).toBeVisible();
  await expect(start).toHaveText(/Start a draft from these/);
  await importer.getByRole('dialog').screenshot({ path: `${EVIDENCE}/import-result-desktop-light.png` });

  // An empty Studio on another device offers the same, once.
  const shots: [string, { width: number; height: number }, boolean][] = [
    ['desktop-light', { width: 1280, height: 800 }, false],
    ['desktop-dark', { width: 1280, height: 800 }, true],
    ['phone-light', { width: 390, height: 844 }, false],
    ['phone-dark', { width: 390, height: 844 }, true],
  ];
  for (const [name, viewport, dark] of shots) {
    const studio = await freshPage(browser, baseUrl, viewport, dark);
    await studio.goto('/studio');
    await expect(studio.getByTestId('studio-starter-offer')).toBeVisible();
    await studio.screenshot({ path: `${EVIDENCE}/studio-offer-${name}.png` });
    await studio.context().close();
  }

  // Taking it from the import result opens the Studio on the new draft.
  await start.click();
  await expect(importer).toHaveURL(/\/studio\?writingId=/);
  const writingId = new URL(importer.url()).searchParams.get('writingId')!;
  for (const passage of PASSAGES) {
    await expect(importer.getByText(passage.text, { exact: false }).first()).toBeVisible();
  }
  await importer.waitForTimeout(500);
  await importer.screenshot({ path: `${EVIDENCE}/draft-opened-desktop-light.png` });

  const draft = await apiGet<{ name: string; content: string }>(baseUrl, `/api/writings/${writingId}`);
  expect(draft.name).toBe('First draft');
  expect(draft.content).toContain('Three passages you highlighted, with their sources.');
  for (const passage of PASSAGES) expect(draft.content).toContain(passage.text);
  const sources = await apiGet<{ noteId?: string; id?: string }[]>(baseUrl, `/api/writings/${writingId}/notes`);
  expect(sources).toHaveLength(3);

  // Once a writing exists, the empty-Studio offer is gone everywhere.
  const later = await freshPage(browser, baseUrl);
  await later.goto('/studio');
  await expect(later.getByText(PASSAGES[0].text, { exact: false }).first()).toBeHidden();
  await expect(later.getByTestId('studio-starter-offer')).toHaveCount(0);
  await later.context().close();
  await importer.context().close();
});
