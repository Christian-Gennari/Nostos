/**
 * Cohesion evidence for the Second Brain, seeded with realistic data.
 *
 * The earlier fixtures were 1-5 trivial topics with no quotes, which is why the
 * design pass missed the quote presentation entirely: nothing on screen exercised
 * it. This seeds several books with mixed note kinds (quote + commentary,
 * quote-only, plain) so every card shape and every index state is represented,
 * then captures desktop and mobile in light and dark.
 *
 * Isolation: the fixture is shared and `visual-regression`'s empty-state test
 * requires it pristine, so this spec records the topic ids that existed before
 * it seeded and deletes only the ones it created (via `cleanupBrain`).
 */
import { expect, test } from '@playwright/test';
import { apiPost, loadFixture } from './support/fixture';
import { cleanupBrain, snapshotTopicIds, type BrainSeed } from './support/brain-fixture';
import { capturePng, DESKTOP_VIEWPORT, MOBILE_VIEWPORT, newCapturePage } from './support/visual-capture';

test.describe.configure({ mode: 'serial' });

const BOOKS: Array<{
  title: string;
  author: string;
  notes: Array<{ content: string; selectedText?: string }>;
}> = [
  {
    title: 'Critique of Practical Reason',
    author: 'Immanuel Kant',
    notes: [
      {
        content:
          'The two sources of awe. Worth returning to when the moral law feels abstract. [[Duty]] [[Moral Law]]',
        selectedText:
          'Two things fill the heart with ever renewed and increasing awe and reverence, the more often and the more steadily we meditate upon them: the starry firmament above and the moral law within.',
      },
      {
        content: '[[Duty]] against inclination — the whole argument in one sentence.',
        selectedText:
          'Duty! Thou sublime and mighty name that dost embrace nothing charming or insinuating.',
      },
      { content: '[[Moral Law]] is the only thing that can be called good without qualification.' },
    ],
  },
  {
    title: 'Meditations',
    author: 'Marcus Aurelius',
    notes: [
      {
        content:
          'On the discipline of attention. This is the passage I keep coming back to. [[Attention]] [[Duty]]',
        selectedText:
          'Confine thyself to the present. Nothing is so conducive to greatness of mind as the ability to examine systematically and honestly everything that meets us.',
      },
      {
        content: '[[Attention]] is a practice, not a talent.',
        selectedText: 'The soul becomes dyed with the colour of its thoughts.',
      },
      { content: '[[Solitude]] and the inner citadel.' },
    ],
  },
  {
    title: 'The World as Will and Representation',
    author: 'Arthur Schopenhauer',
    notes: [
      { content: '[[Memory]] and the persistence of the will.' },
      { content: '[[Solitude]] — the argument that company degrades. [[Attention]]' },
      {
        content: '[[Reading]] as thinking in someone else’s head.',
        selectedText:
          'When we read, another person thinks for us: we merely repeat his mental process. It is the same as the pupil, in learning to write, following with his pen the lines that the teacher has written.',
      },
    ],
  },
  {
    title: 'The Order of Time',
    author: 'Carlo Rovelli',
    notes: [
      { content: '[[Memory]] is what lets us construct a past at all.' },
      { content: '[[Attention]] to the present moment, described physically.' },
    ],
  },
];

let seed: BrainSeed | null = null;

async function ensureSeed(fixture: ReturnType<typeof loadFixture>): Promise<BrainSeed> {
  if (seed) return seed;

  // Snapshot BEFORE creating anything, so cleanup can restore the fixture exactly.
  const beforeTopicIds = await snapshotTopicIds(fixture.baseUrl);
  const firstBook = await apiPost<{ id: string }>(fixture.baseUrl, '/api/books', {
    type: 'physical',
    title: BOOKS[0].title,
    author: BOOKS[0].author,
    categories: 'coh',
  });

  for (const book of BOOKS) {
    // Reuse the first book rather than creating it twice.
    const bookId =
      book === BOOKS[0]
        ? firstBook.id
        : (
            await apiPost<{ id: string }>(fixture.baseUrl, '/api/books', {
              type: 'physical',
              title: book.title,
              author: book.author,
              categories: 'coh',
            })
          ).id;

    for (const note of book.notes) {
      await apiPost(fixture.baseUrl, `/api/books/${bookId}/notes`, {
        content: note.content,
        ...(note.selectedText ? { selectedText: note.selectedText } : {}),
      });
    }
  }

  seed = { bookId: firstBook.id, topicNames: [], beforeTopicIds };
  return seed;
}

const THEMES = ['light', 'dark'] as const;

for (const theme of THEMES) {
  test(`topic pane with quotes — ${theme}`, async ({ browser }) => {
    const fixture = loadFixture();
    await ensureSeed(fixture);

    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
        try {
          localStorage.setItem('nostos.theme', t);
        } catch {
          /* ignore */
        }
      }, theme);
      await page.locator('.index-item').first().click();
      await page.locator('.note-card').first().waitFor({ timeout: 30_000 });
      await page.waitForTimeout(600);

      // A quote must actually be on screen for this capture to mean anything.
      const quotes = await page.locator('.note-quote').count();
      console.log(`QUOTES RENDERED (${theme}):`, quotes);
      expect(quotes, 'seeded quote notes must render quotes').toBeGreaterThan(0);

      // The topic's notes are a READING COLUMN, not a board: one passage per
      // row, all starting on the same left edge, each quotation held to a
      // reading measure, with its commentary beside it in the margin.
      //
      // This replaces the earlier "the board must board" assertions. The board
      // put notes side by side in boxed cards, which read well as a wall of
      // notes and badly as text: two columns of boxes, chrome repeated around
      // every passage, and a size limit that forced long quotations to
      // collapse. The defect those assertions pinned (a card stranded against
      // the left edge with a dead band beside it) is still pinned here in its
      // new form: the space right of the quotation is used by the commentary,
      // and the measure is asserted directly.
      const measured = await page.evaluate(() => {
        const column = document.querySelector('.reading-column') as HTMLElement | null;
        if (!column) return null;
        const columnRect = column.getBoundingClientRect();
        const passages = Array.from(column.querySelectorAll('.note-card')) as HTMLElement[];
        return {
          columnLeft: Math.round(columnRect.left),
          groups: column.querySelectorAll('.passage-group').length,
          boards: document.querySelectorAll('.cards-grid').length,
          passages: passages.map((passage) => {
            const r = passage.getBoundingClientRect();
            const quote = passage.querySelector('.quote-text') as HTMLElement | null;
            const commentary = passage.querySelector('.note-quote ~ .note-text') as HTMLElement | null;
            const q = quote?.getBoundingClientRect();
            const c = commentary?.getBoundingClientRect();
            return {
              left: Math.round(r.left),
              top: Math.round(r.top),
              quoteWidth: q ? Math.round(q.width) : -1,
              quoteRight: q ? Math.round(q.right) : -1,
              quoteStyle: quote ? getComputedStyle(quote).fontStyle : '',
              commentaryLeft: c ? Math.round(c.left) : -1,
              commentaryTop: c ? Math.round(c.top) : -1,
              quoteTop: q ? Math.round(q.top) : -1,
            };
          }),
        };
      });

      console.log(`READING COLUMN (${theme}):`, JSON.stringify(measured));
      expect(measured, '.reading-column must render').not.toBeNull();
      expect(measured!.boards, 'the card board must be gone').toBe(0);
      expect(measured!.groups, 'passages are grouped under their book').toBeGreaterThan(0);

      const passages = measured!.passages;
      expect(passages.length, 'at least one passage must render').toBeGreaterThan(0);

      // One passage per row, every one on the column's left edge.
      expect(
        new Set(passages.map((p) => p.top)).size,
        'passages must not sit beside each other',
      ).toBe(passages.length);
      for (const p of passages) {
        expect(Math.abs(p.left - measured!.columnLeft), 'a passage starts at the column edge').toBeLessThanOrEqual(2);
      }

      for (const p of passages) {
        if (p.quoteWidth < 0) continue;
        // 39rem less the rule and its gutter; a wider quotation is past a
        // comfortable line length, a much narrower one wraps every few words.
        expect(p.quoteWidth, 'a quotation keeps to the reading measure').toBeLessThanOrEqual(624);
        expect(p.quoteWidth, 'a quotation is not squeezed on desktop').toBeGreaterThanOrEqual(440);
        expect(p.quoteStyle, 'a passage is set upright').toBe('normal');

        if (p.commentaryLeft < 0) continue;
        // Commentary is a marginal note: right of the quotation, level with it.
        expect(p.commentaryLeft, 'commentary sits in the margin').toBeGreaterThan(p.quoteRight);
        expect(Math.abs(p.commentaryTop - p.quoteTop), 'commentary starts level with the quotation').toBeLessThanOrEqual(12);
      }

      await capturePng(page, `brain-cohesion-quotes-${theme}`);
    } finally {
      await context.close();
    }
  });

  test(`Notes inspector shares topic evidence presentation — ${theme}`, async ({ browser }) => {
    const fixture = loadFixture();
    await ensureSeed(fixture);

    const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
        try {
          localStorage.setItem('nostos.theme', t);
        } catch {
          /* ignore */
        }
      }, theme);

      await page.getByRole('button', { name: 'Notes', exact: true }).click();
      const search = page.locator('#brain-all-notes-search');
      await search.waitFor({ timeout: 30_000 });
      await search.fill('soul becomes dyed');
      const row = page.locator('.note-row-item').first();
      await row.waitFor({ timeout: 30_000 });
      await row.click();

      const inspector = page.locator('[data-testid="brain-note-inspector"]');
      await inspector.locator('app-note-card .quote-text').waitFor({ timeout: 30_000 });
      expect(await inspector.locator('.quote-text').textContent()).toContain('soul becomes dyed');
      expect(await inspector.locator('.note-text').textContent()).toContain('practice, not a talent');
      expect(await inspector.locator('.review-quote').count()).toBe(0);
      expect(await page.locator('.brain-header #brain-all-notes-search').count()).toBe(1);
      expect(await page.locator('.index-col #brain-all-notes-search').count()).toBe(0);

      await capturePng(page, `brain-cohesion-notes-${theme}`);
    } finally {
      await context.close();
    }
  });

  test(`Notes inspector remains readable on mobile — ${theme}`, async ({ browser }) => {
    const fixture = loadFixture();
    await ensureSeed(fixture);

    const { context, page } = await newCapturePage(browser, MOBILE_VIEWPORT, true);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
        try {
          localStorage.setItem('nostos.theme', t);
        } catch {
          /* ignore */
        }
      }, theme);

      await page.getByRole('button', { name: 'Notes', exact: true }).click();
      const search = page.locator('#brain-all-notes-search');
      await search.waitFor({ timeout: 30_000 });
      await search.fill('soul becomes dyed');
      const row = page.locator('.note-row-item').first();
      await row.waitFor({ timeout: 30_000 });
      await row.click();

      const inspector = page.locator('[data-testid="brain-note-inspector"]');
      await inspector.locator('app-note-card .quote-text').waitFor({ timeout: 30_000 });

      const horizontalOverflow = await page.evaluate(() => {
        const root = document.documentElement;
        return root.scrollWidth - root.clientWidth;
      });
      expect(horizontalOverflow, 'Notes inspector must not create horizontal page overflow').toBeLessThanOrEqual(1);

      await capturePng(page, `brain-cohesion-notes-mobile-${theme}`);
    } finally {
      await context.close();
    }
  });

  test(`index list on mobile — ${theme}`, async ({ browser }) => {
    const fixture = loadFixture();
    await ensureSeed(fixture);

    const { context, page } = await newCapturePage(browser, MOBILE_VIEWPORT, true);
    try {
      await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
      await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-theme', t);
        try {
          localStorage.setItem('nostos.theme', t);
        } catch {
          /* ignore */
        }
      }, theme);
      await page.waitForTimeout(400);
      await capturePng(page, `brain-cohesion-index-mobile-${theme}`);
    } finally {
      await context.close();
    }
  });
}

test.afterAll(async () => {
  if (!seed) return;
  const fixture = loadFixture();
  await cleanupBrain(fixture.baseUrl, seed);
});
