/**
 * Passages in Brain (issue #1020): the Passages filter lists only passages,
 * a passage row leads with its title, the open passage is a passage card,
 * and Read passage opens the passage viewer over Brain without leaving it,
 * from the notes view and from a topic.
 */
import { expect, test } from '@playwright/test';

import { apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

const PARAGRAPHS = [
  '“Even this must have a preface—that is, a literary preface,” laughed Ivan, “and I am a poor hand at making one.',
  'You see, my action takes place in the sixteenth century, and at that time, as you probably learnt at school, it was customary in poetry to bring down heavenly powers on earth.',
  'My story is laid in Spain, in Seville, in the most terrible time of the Inquisition, when fires were lighted every day to the glory of God.',
];

let seed: BrainSeed;
let topic: string;

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  const run = newRunId();
  topic = `Freedom${run}`;
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', {
    type: 'physical',
    title: `The Brothers Karamazov ${run}`,
    author: 'Fyodor Dostoyevsky',
  });
  seed = { bookId: book.id, topicNames: [topic], beforeTopicIds };

  // Long enough to need more than one page in the viewer.
  const text = Array.from({ length: 14 }, () => PARAGRAPHS.join('\n\n')).join('\n\n');
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, {
    content: `Ivan's poem. [[${topic}]]`,
    kind: 'passage',
    passageTitle: 'The Grand Inquisitor',
    sourceAnchorKind: 'epub_cfi',
    sourceAnchorValue: 'epubcfi(/6/80!/4/2/1:0)',
    passageEnd: 'epubcfi(/6/80!/4/112/1:285)',
    selectedText: text,
    passageHtml: text.split('\n\n').map((p) => `<p>${p}</p>`).join(''),
  });
  await apiPost(baseUrl, `/api/books/${book.id}/notes`, {
    content: `A short thought about [[${topic}]].`,
  });
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

test('the Passages place lists passages, and one opens in the viewer over Brain', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/passages?q=${encodeURIComponent('Grand Inquisitor')}`, { waitUntil: 'domcontentloaded' });

  await expect(page.getByRole('heading', { name: 'Passages', level: 1 })).toBeVisible();
  const card = page.locator('app-note-item', { hasText: 'The Grand Inquisitor' }).first();
  await expect(card.locator('.note-item-passage-title')).toHaveText('The Grand Inquisitor');
  await expect(card.locator('.note-item-passage-size')).toContainText('words');
  // Notes that are not passages stay out of the place.
  await expect(page.locator('app-note-item', { hasText: 'A short thought about' })).toHaveCount(0);

  await card.getByRole('button', { name: 'Read passage' }).click();
  await expect(page.getByRole('dialog', { name: 'The Grand Inquisitor' })).toBeVisible();
  await expect(page.getByTestId('passage-viewer-text')).toContainText('Even this must have a preface');
  await expect(page).toHaveURL(/\/brain\/passages/);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'The Grand Inquisitor' })).toHaveCount(0);
});

test('a passage in a topic is a passage card that opens the viewer', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const topicId = await findTopicId(baseUrl, topic);
  expect(topicId).not.toBeNull();
  await page.goto(`${baseUrl}/brain/topics/${topicId}`, { waitUntil: 'domcontentloaded' });

  const card = page.locator('app-note-item', { hasText: 'The Grand Inquisitor' }).first();
  await expect(card.locator('.note-item-passage-title')).toHaveText('The Grand Inquisitor');
  await card.getByRole('button', { name: 'Read passage' }).click();
  await expect(page.getByTestId('passage-viewer-page')).toHaveText(/Page 1 of \d+/);
  await page.getByTestId('passage-viewer-next').click();
  await expect(page.getByTestId('passage-viewer-page')).toHaveText(/Page 2 of \d+/);
});
