/**
 * The rebuilt Brain's lists (docs/brain.md): adding and removing topics on a
 * note, Undo, adding a topic to several selected notes, narrowing a topic to
 * the notes it shares with another, and deleting a note. Every change is
 * checked against the API, not only the screen.
 */
import { expect, test, type Page } from '@playwright/test';

import { apiGet, apiPost, loadFixture, newRunId } from './support/fixture';
import { cleanupBrain, findTopicId, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';

test.use({ serviceWorkers: 'block' });

interface Hit {
  id: string;
  content: string;
  topicNames: string[];
}

let seed: BrainSeed;
let run: string;
let bookTitle: string;
let ethics: string;
let virtue: string;
const noteIds: Record<string, string> = {};

test.beforeEach(async () => {
  const { baseUrl } = loadFixture();
  run = newRunId();
  bookTitle = `Brain Lists ${run}`;
  ethics = `Ethics${run}`;
  virtue = `Virtue${run}`;
  const beforeTopicIds = await snapshotTopicIds(baseUrl);
  const book = await apiPost<{ id: string }>(baseUrl, '/api/books', { type: 'physical', title: bookTitle, author: 'Nostos QA' });
  seed = { bookId: book.id, topicNames: [ethics, virtue], beforeTopicIds };
  const notes: Record<string, string> = {
    both: `Both topics here. [[${ethics}]] [[${virtue}]]`,
    ethicsOnly: `Only one. [[${ethics}]]`,
    loose1: `A loose thought ${run} one.`,
    loose2: `A loose thought ${run} two.`,
  };
  for (const [key, content] of Object.entries(notes)) {
    const note = await apiPost<{ id: string }>(baseUrl, `/api/books/${book.id}/notes`, { content });
    noteIds[key] = note.id;
  }
});

test.afterEach(async () => {
  await cleanupBrain(loadFixture().baseUrl, seed);
});

async function note(id: string): Promise<Hit> {
  return apiGet<Hit>(loadFixture().baseUrl, `/api/notes/${id}`);
}

function card(page: Page, id: string) {
  return page.locator(`app-note-item[data-note-id="${id}"]`);
}

test('adds a topic from the picker, removes it, and Undo puts it back', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/unsorted?q=${encodeURIComponent(run)}`);
  const loose = card(page, noteIds['loose1']);
  await expect(loose).toBeVisible();

  await loose.getByRole('button', { name: 'Add a topic' }).click();
  const picker = page.getByRole('combobox', { name: 'Add a topic to this note' });
  await picker.fill(`New${run}`);
  await page.getByRole('option', { name: `Create “New${run}”` }).click();

  // The note gained a topic, so it leaves Unsorted.
  await expect(loose).toHaveCount(0);
  await expect.poll(async () => (await note(noteIds['loose1'])).topicNames).toEqual([`New${run}`]);

  // On the topic's page, remove it again; Undo restores it.
  const newId = await findTopicId(baseUrl, `New${run}`);
  await page.goto(`${baseUrl}/brain/topics/${newId}`);
  await card(page, noteIds['loose1']).getByRole('button', { name: `Remove topic New${run}` }).click();
  await expect.poll(async () => (await note(noteIds['loose1'])).topicNames).toEqual([]);
  expect((await note(noteIds['loose1'])).content).toBe(`A loose thought ${run} one.`);

  // Removing the page's own topic takes the note off the page...
  await expect(card(page, noteIds['loose1'])).toHaveCount(0);

  await page.getByRole('button', { name: 'Undo' }).click();
  await expect.poll(async () => (await note(noteIds['loose1'])).topicNames).toEqual([`New${run}`]);
  // ...and Undo brings it back.
  await expect(card(page, noteIds['loose1'])).toBeVisible();
});

test('keeps the search field as typed, trailing space and all', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain`);
  const field = page.getByRole('searchbox', { name: 'Search all notes' });
  await field.pressSequentially('loose ', { delay: 30 });
  await page.waitForTimeout(600);
  await expect(field).toHaveValue('loose ');
  await field.pressSequentially('thought', { delay: 30 });
  await expect(page).toHaveURL(/q=loose(\+|%20)thought/);
  await expect(card(page, noteIds['loose1'])).toBeVisible();
});

test('adds one topic to several selected notes, with Undo for exactly those notes', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain/unsorted?q=${encodeURIComponent(run)}`);
  await expect(card(page, noteIds['loose2'])).toBeVisible();

  await page.getByRole('button', { name: 'Select', exact: true }).click();
  for (const key of ['loose1', 'loose2']) {
    await card(page, noteIds[key]).getByRole('checkbox').check();
  }
  await expect(page.getByRole('toolbar', { name: 'Selected notes' })).toContainText('2 selected');

  await page.getByRole('button', { name: 'Add topic…' }).click();
  await page.getByRole('combobox', { name: 'Add a topic to the selected notes' }).fill(virtue);
  await page.getByRole('option', { name: virtue, exact: false }).first().click();

  await expect(page.getByText(`Added “${virtue}” to 2 notes`)).toBeVisible();
  for (const key of ['loose1', 'loose2']) {
    await expect.poll(async () => (await note(noteIds[key])).topicNames).toEqual([virtue]);
  }

  await page.getByRole('button', { name: 'Undo' }).click();
  for (const key of ['loose1', 'loose2']) {
    await expect.poll(async () => (await note(noteIds[key])).topicNames).toEqual([]);
    expect((await note(noteIds[key])).content).not.toContain('[[');
  }
});

test('Back returns to the list where the reader left it', async ({ page }) => {
  const { baseUrl } = loadFixture();
  for (let i = 0; i < 30; i++) {
    await apiPost(baseUrl, `/api/books/${seed.bookId}/notes`, { content: `Filler ${run} ${i}. ` + 'A longer line of text. '.repeat(6) });
  }
  await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(run)}&order=oldest`);
  const scroller = page.locator('.brain-scroller');
  await expect(card(page, noteIds['both'])).toBeVisible();
  await scroller.evaluate((el) => el.scrollTo({ top: 1500 }));
  await page.waitForTimeout(300);

  // Leave in the app (a sidebar topic), then come Back.
  await page.locator('.topic-row', { hasText: ethics }).click();
  await expect(card(page, noteIds['ethicsOnly'])).toBeVisible();
  await page.goBack();

  await expect(card(page, noteIds['both'])).toBeAttached();
  await expect.poll(() => scroller.evaluate((el) => el.scrollTop)).toBeGreaterThan(1000);
});

test('narrows a topic to the notes it shares with another', async ({ page }) => {
  const { baseUrl } = loadFixture();
  const ethicsId = await findTopicId(baseUrl, ethics);
  await page.goto(`${baseUrl}/brain/topics/${ethicsId}`);
  await expect(card(page, noteIds['ethicsOnly'])).toBeVisible();

  await page.getByRole('region', { name: 'Often with' }).getByRole('button', { name: virtue }).click();
  await expect(page).toHaveURL(/with=/);
  await expect(card(page, noteIds['both'])).toBeVisible();
  await expect(card(page, noteIds['ethicsOnly'])).toHaveCount(0);

  await page.getByRole('button', { name: 'Show all' }).click();
  await expect(card(page, noteIds['ethicsOnly'])).toBeVisible();
});

test('deletes a note from its menu after confirming', async ({ page }) => {
  const { baseUrl } = loadFixture();
  await page.goto(`${baseUrl}/brain?q=${encodeURIComponent(`${run} two`)}`);
  const target = card(page, noteIds['loose2']);
  await expect(target).toBeVisible();

  await target.getByRole('button', { name: 'More actions for this note' }).click();
  await page.getByRole('menuitem', { name: 'Delete note' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();

  await expect(target).toHaveCount(0);
  const res = await fetch(`${baseUrl}/api/notes/${noteIds['loose2']}`);
  expect(res.status).toBe(404);
});
