/**
 * Cohesion evidence for the Second Brain, seeded with realistic data.
 *
 * The earlier fixtures were 1-5 trivial concepts with no quotes, which is why the
 * design pass missed the quote presentation entirely: nothing on screen exercised
 * it. This seeds several books with mixed note kinds (quote + commentary,
 * quote-only, plain) so every card shape and every index state is represented,
 * then captures desktop and mobile in light and dark.
 *
 * Isolation: the fixture is shared and `visual-regression`'s empty-state test
 * requires it pristine, so this spec records the concept ids that existed before
 * it seeded and deletes only the ones it created (via `cleanupBrain`).
 */
import { expect, test } from '@playwright/test';
import { apiPost, loadFixture } from './support/fixture';
import { cleanupBrain, snapshotConceptIds, type BrainSeed } from './support/brain-fixture';
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
  const beforeConceptIds = await snapshotConceptIds(fixture.baseUrl);
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

  seed = { bookId: firstBook.id, conceptNames: [], beforeConceptIds };
  return seed;
}

const THEMES = ['light', 'dark'] as const;

for (const theme of THEMES) {
  test(`concept pane with quotes — ${theme}`, async ({ browser }) => {
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

      // The board must actually board: notes sit BESIDE each other whenever there
      // is more than one, and no note is stranded against the left edge of a row
      // with empty pane to its right.
      //
      // The defect these assertions pin, in order of how it was found:
      //  (1) The first attempt capped the quote text at 62ch INSIDE a full-width
      //      card, leaving a 360px dead band right of the quotation and two
      //      different measures in one card.
      //  (2) The second attempt capped the CARD at 44rem but LEFT the
      //      `grid-column: 1 / -1` full-row span in place, so a 704px card sat
      //      against the left edge of a 1016px row with a 312px void down the
      //      right of the pane — and because most notes carry a quotation, every
      //      note was forced onto its own row and no two could ever sit side by
      //      side. That is why the pane read as "a stack of cards".
      // An assertion that only measures inside the card cannot see (2): it is a
      // PANE-level defect. So measure the pane.
      const measured = await page.evaluate(() => {
        const grid = document.querySelector('.cards-grid') as HTMLElement | null;
        if (!grid) return null;
        const gridRect = grid.getBoundingClientRect();
        const cards = Array.from(document.querySelectorAll('.note-card')) as HTMLElement[];
        return {
          gridLeft: Math.round(gridRect.left),
          gridRight: Math.round(gridRect.right),
          gridWidth: Math.round(gridRect.width),
          cards: cards.map((card) => {
            const r = card.getBoundingClientRect();
            const quote = card.querySelector('.quote-text') as HTMLElement | null;
            const commentary = card.querySelector('.note-text') as HTMLElement | null;
            return {
              left: Math.round(r.left),
              right: Math.round(r.right),
              top: Math.round(r.top),
              width: Math.round(r.width),
              quoteRight: quote ? Math.round(quote.getBoundingClientRect().right) : -1,
              commentaryRight: commentary ? Math.round(commentary.getBoundingClientRect().right) : -1,
              // Empty pane between this card's right edge and the grid's: zero for
              // a card that fills its track.
              voidRight: Math.round(gridRect.right - r.right),
            };
          }),
        };
      });

      console.log(`BOARD LAYOUT (${theme}):`, JSON.stringify(measured));
      expect(measured, '.cards-grid must render').not.toBeNull();

      const cards = measured!.cards;
      expect(cards.length, 'at least one note card must render').toBeGreaterThan(0);

      // (2) No card may be stranded: it must reach the right edge of its grid, so
      // the pane carries no dead band beside a note. A wrapped card legitimately
      // lands in the last still-open track, so the assertion is on the GRID's
      // right edge being reached by at least one card in the final row, plus no
      // single card sitting far short of it.
      if (cards.length === 1) {
        // The one-note case is deliberately narrower than the pane (a 44rem
        // reading measure), and centred — so assert it is CENTRED, which is what
        // makes it read as intentional rather than as a failed board.
        const leftGap = cards[0].left - measured!.gridLeft;
        const rightGap = measured!.gridRight - cards[0].right;
        console.log(`SINGLE NOTE GAPS (${theme}): left=${leftGap} right=${rightGap}`);
        expect(
          Math.abs(leftGap - rightGap),
          'a lone note must be centred in the pane, not stranded on the left',
        ).toBeLessThanOrEqual(2);
      } else {
        // With more than one note the board must actually place notes beside each
        // other: at least two cards share a row.
        const byTop = new Map<number, number[]>();
        for (const c of cards) {
          const row = byTop.get(c.top) ?? [];
          row.push(c.left);
          byTop.set(c.top, row);
        }
        const rows = [...byTop.values()].map((lefts) => lefts.sort((a, b) => a - b));
        console.log(`CARDS PER ROW (${theme}):`, JSON.stringify(rows.map((r) => r.length)));
        expect(
          Math.max(...rows.map((r) => r.length)),
          'notes must sit beside each other on the board (not one per row)',
        ).toBeGreaterThanOrEqual(2);

        // Every row must be laid out edge to edge across the grid: it starts at
        // the grid's left edge and its rightmost card reaches the grid's right
        // edge. A row that stops short is the "stack of cards with dead space"
        // defect — which is exactly what a forced `grid-column: 1 / -1` plus a
        // card `max-width` produced.
        for (const lefts of rows) {
          expect(
            Math.abs(lefts[0] - measured!.gridLeft),
            'a row must start at the grid edge',
          ).toBeLessThanOrEqual(2);
          const lastRight = Math.max(
            ...cards.filter((c) => lefts.includes(c.left)).map((c) => c.right),
          );
          expect(
            Math.abs(lastRight - measured!.gridRight),
            'a row must reach the grid right edge (no dead pane band)',
          ).toBeLessThanOrEqual(2);
        }
      }

      // Quote and its commentary must share ONE measure (right edges within 2px).
      for (const c of cards) {
        if (c.quoteRight < 0 || c.commentaryRight < 0) continue;
        expect(
          Math.abs(c.quoteRight - c.commentaryRight),
          'quote and its commentary must share one measure',
        ).toBeLessThanOrEqual(2);
      }

      await capturePng(page, `brain-cohesion-quotes-${theme}`);
    } finally {
      await context.close();
    }
  });

  test(`Notes inspector shares concept evidence presentation — ${theme}`, async ({ browser }) => {
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
