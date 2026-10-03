/**
 * Measure the note-card hollow-middle fix in a real browser.
 *
 * Asserts, per note card, that the gap between the end of the note content and
 * the start of the card's metadata footer is small — i.e. the card hugs its
 * content instead of stretching to a forced height. Before the fix the gap was
 * ~80-90px in short cards; afterwards it should be a normal spacing value.
 *
 * Also captures a screenshot so the result can be eyeballed.
 */
import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain } from './support/brain-fixture';
import {
  capturePng,
  DESKTOP_VIEWPORT,
  newCapturePage,
} from './support/visual-capture';

test('brain note cards hug their content (no hollow middle)', async ({ browser }) => {
  const fixture = loadFixture();

  // Seed a realistic mix: one long note, several short ones, so the grid has
  // both tall and short cards and any forced height shows up immediately.
  // `seedBrain` records what already existed so `cleanupBrain` can restore the
  // fixture: the visual matrix's empty-state test runs later against this same
  // database and requires it to be free of topics.
  const seed = await seedBrain(
    fixture.baseUrl,
    `Hollow Check ${Date.now().toString(36)}`,
    [
      'A very short note on [[Gapcheck]].',
      'Another short one about [[Gapcheck]] and [[Spacing]].',
      'A deliberately long note on [[Gapcheck]] that runs to several lines so the grid contains both a tall card and short ones, which is exactly the situation where a forced card height leaves a hollow middle in the short cards and makes the row look broken.',
      'Short again with [[Spacing]].',
    ],
    ['Gapcheck', 'Spacing']
  );

  const { context, page } = await newCapturePage(browser, DESKTOP_VIEWPORT);
  try {
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    // Select the seeded topic so its notes render.
    await page.locator('.index-item', { hasText: 'Gapcheck' }).first().click();
    await page.locator('.note-card').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(600);

    const measured = await page.evaluate(() => {
      const out: Array<Record<string, number>> = [];
      for (const card of Array.from(document.querySelectorAll('.note-card'))) {
        const inner = card.querySelector('.note-card-container') as HTMLElement | null;
        const body = card.querySelector('.collapsible-body') as HTMLElement | null;
        const meta = card.querySelector('.note-metadata') as HTMLElement | null;
        const expand = card.querySelector('.expand-btn') as HTMLElement | null;
        if (!inner || !body || !meta) continue;
        // The footer must follow the content that precedes it. When an expand
        // button is present it is legitimate content between the two, so measure
        // from that instead of from the body.
        const preceding = expand ? expand.getBoundingClientRect() : body.getBoundingClientRect();
        out.push({
          cardHeight: Math.round(card.getBoundingClientRect().height),
          gap: Math.round(meta.getBoundingClientRect().top - preceding.bottom),
        });
      }
      return out;
    });

    console.log('MEASURED CARDS:', JSON.stringify(measured, null, 1));

    const png = await capturePng(page, 'brain-note-cards-hollow-check');
    console.log('PNG:', png);

    expect(measured.length, 'at least one note card must render').toBeGreaterThan(0);

    // No card may carry the old ~90px dead band. Allow a generous 28px (normal
    // spacing) so the assertion tests the defect, not pixel perfection.
    const worst = Math.max(...measured.map((m) => m.gap));
    console.log('WORST GAP (px):', worst);
    expect(worst, 'no note card may have a hollow middle').toBeLessThanOrEqual(28);
  } finally {
    await context.close();
    await cleanupBrain(fixture.baseUrl, seed);
  }
});
