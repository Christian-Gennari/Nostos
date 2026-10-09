/**
 * Book detail visual evidence — the cover echo (cover-derived background wash).
 *
 * Book detail needs a real book WITH cover art (the echo is derived from the
 * cover), so this spec is real-library only and skips with a documented reason
 * otherwise — same contract as the reader surfaces in visual-regression.spec.ts.
 *
 *   VISUAL_QA_LIBRARY_URL=<library-origin> npm run e2e -- book-detail-visual.spec.ts
 *
 * The URL must serve the build under test and have at least one book with a
 * cover. Both viewports are captured from this one spec via newCapturePage's own
 * contexts (the harness pattern), so the file name deliberately avoids the
 * "mobile" pattern that the desktop project ignores.
 *
 * Evidence: e2e/visual-evidence/book-detail-hero-{desktop,mobile}.{png,json}
 */
import { expect, test } from '@playwright/test';

import {
  capturePng,
  checkBookDetailHero,
  DESKTOP_VIEWPORT,
  findLibraryCoverBook,
  LIBRARY_URL,
  MOBILE_VIEWPORT,
  newCapturePage,
  writeGeometryReport,
  type CaptureMeta,
  type GeometryCheck,
} from './support/visual-capture';

/**
 * A gradient stop list can only be judged from its numbers: a "harsh" fade is not
 * a styling preference, it is a measurable shape defect. Two versions of this
 * hero shipped a visible band, and both are visible in the stops:
 *   - the scrim STRENGTHENED downward (.28 -> .52) exactly where the fade had to
 *     lighten, so the luminance dipped and then jumped;
 *   - the fade ramp ACCELERATED mid-way (.5@52% -> .9@80%) and then stopped dead
 *     at 0/px, so the transition got steeper half way down and hit a wall.
 * What a smooth fade requires: the scrim releases monotonically downward, the
 * fade's alpha rises monotonically to full, and its per-segment slope rises then
 * falls (an ease-in-out) rather than accelerating after the peak.
 */
function parseStops(backgroundImage: string): Array<{ a: number; pos: number }> {
  // Position is OPTIONAL. Minification drops the redundant `0%` / `100%` from a
  // gradient's endpoints (and rewrites rgba() to hex), and Chrome serialises a
  // positionless stop back without one. Requiring a percentage therefore
  // silently dropped the FIRST and LAST stop of the scrim whenever this spec ran
  // against a production build instead of the dev server — the guard covered 3
  // of 5 stops and said nothing. `pos: -1` marks "no explicit position", which is
  // valid CSS and only surfaces in a failure message.
  const re = /rgba?\(([^)]+)\)(?:\s+([\d.]+)%)?/g;
  const out: Array<{ a: number; pos: number }> = [];
  let m: RegExpExecArray | null;
  while ((m = re.exec(backgroundImage)) !== null) {
    const parts = m[1].split(',').map((v) => parseFloat(v.trim()));
    out.push({ a: parts.length >= 4 ? parts[3] : 1, pos: m[2] ? parseFloat(m[2]) : -1 });
  }
  return out;
}

async function checkBookDetailFade(page: import('@playwright/test').Page): Promise<GeometryCheck> {
  const raw = await page.evaluate(() => {
    const cs = (s: string) => {
      const el = document.querySelector(s);
      return el ? getComputedStyle(el).backgroundImage : '';
    };
    return {
      fade: cs('.art-fade'),
      scrim: cs('.art-scrim'),
      fadeHeight: Math.round(document.querySelector('.art-fade')!.getBoundingClientRect().height),
    };
  });

  const fade = parseStops(raw.fade);
  const scrim = parseStops(raw.scrim);
  const problems: string[] = [];

  if (fade.length < 3) problems.push(`fade gradient unparseable (${raw.fade})`);
  if (scrim.length < 3) problems.push(`scrim gradient unparseable (${raw.scrim})`);

  // Fade alpha: non-decreasing, ending fully opaque.
  for (let i = 1; i < fade.length; i++) {
    if (fade[i].a < fade[i - 1].a - 0.001) problems.push(`fade alpha decreases at ${fade[i].pos}%`);
  }
  if (fade.length && Math.abs(fade[fade.length - 1].a - 1) > 0.001) {
    problems.push(`fade does not reach full opacity (ends at ${fade[fade.length - 1].a})`);
  }

  // Scrim alpha: non-increasing (it must release downward, never strengthen).
  for (let i = 1; i < scrim.length; i++) {
    if (scrim[i].a > scrim[i - 1].a + 0.001) {
      problems.push(`scrim STRENGTHENS at ${scrim[i].pos}% (${scrim[i - 1].a} -> ${scrim[i].a})`);
    }
  }

  // Fade slope: rises then falls (ease-in-out), no acceleration after the peak.
  const slopes: number[] = [];
  for (let i = 1; i < fade.length; i++) {
    const dPos = fade[i].pos - fade[i - 1].pos;
    if (dPos <= 0) continue;
    slopes.push((fade[i].a - fade[i - 1].a) / dPos);
  }
  const peak = slopes.indexOf(Math.max(...slopes));
  for (let i = peak + 1; i < slopes.length; i++) {
    if (slopes[i] > slopes[i - 1] + 1e-4) {
      problems.push(`fade ACCELERATES after its peak (segment ${i}: ${slopes[i].toFixed(5)}/%)`);
    }
  }
  // Both ends must be GENTLE relative to the peak. This is the rule that actually
  // catches the shipped defect: the old ramp's first segment was 0.0096/% against
  // a peak of 0.0143/% — 67% of peak, so it left the flat dark band at almost full
  // speed and drew a visible onset line across the hero. A cubic ease-in-out sits
  // near 30%. Half the peak is the honest boundary between the two.
  if (slopes.length >= 3) {
    const pk = Math.max(...slopes);
    if (slopes[0] > pk * 0.5) problems.push(`fade starts too steeply (${slopes[0].toFixed(5)}/%, peak ${pk.toFixed(5)}/%) — visible onset line`);
    if (slopes[slopes.length - 1] > pk * 0.5) problems.push(`fade ends too steeply (${slopes[slopes.length - 1].toFixed(5)}/%, peak ${pk.toFixed(5)}/%) — visible stop line`);
  }

  const ok = problems.length === 0;
  const metrics = { fade, scrim, slopes, fadeHeight: raw.fadeHeight };
  const msg = ok
    ? `fade is a smooth ease-in-out over ${raw.fadeHeight}px: scrim releases monotonically ` +
      `(${scrim.map((s) => s.a).join(' -> ')}), fade alpha rises to full ` +
      `(${fade.map((s) => s.a).join(' -> ')}), slope peaks at segment ${peak} then only decreases`
    : `fade is not smooth: ${problems.join('; ')}`;
  return ok
    ? { id: 'book-detail-fade', pass: true, message: msg, metrics }
    : { id: 'book-detail-fade', pass: false, message: msg, metrics };
}

const SKIP_REASON =
  'book detail needs a real book with cover art, which the isolated e2e fixture cannot ' +
  'provide (its books have no covers and the harness never invents assets). Run with ' +
  '`VISUAL_QA_LIBRARY_URL=<origin>` against an instance serving the build under test ' +
  '(see docs/visual-verification.md §Book detail).';

interface Case {
  name: string;
  viewport: { width: number; height: number };
  mobile: boolean;
}

const CASES: Case[] = [
  { name: 'book-detail-hero-desktop', viewport: DESKTOP_VIEWPORT, mobile: false },
  { name: 'book-detail-hero-mobile', viewport: MOBILE_VIEWPORT, mobile: true },
];

test.describe('visual matrix — Book detail (real library)', () => {
  for (const tc of CASES) {
    test(`${tc.name}`, async ({ browser }) => {
      test.skip(!LIBRARY_URL, SKIP_REASON);

      const book = await findLibraryCoverBook();
      test.skip(
        !book,
        `no book with cover art found at VISUAL_QA_LIBRARY_URL (${LIBRARY_URL}) — add one ` +
          `to the real library to capture '${tc.name}' (see docs/visual-verification.md §Book detail)`
      );

      const { context, page } = await newCapturePage(browser, tc.viewport, tc.mobile);
      try {
        await page.goto(`${LIBRARY_URL}/library/${book!.id}`, { waitUntil: 'domcontentloaded' });
        await page.locator('.book-title').first().waitFor({ timeout: 30_000 });
        // Let the cover, its thumbnail and the blurred layer paint.
        await page.waitForTimeout(800);

        // Evidence first: the PNG is produced even if a check below fails, so a
        // failing run still leaves something to look at.
        const png = await capturePng(page, tc.name);
        expect(png).toContain(tc.name);

        const check = await checkBookDetailHero(page);
        const fadeCheck = await checkBookDetailFade(page);
        const meta: CaptureMeta = {
          name: tc.name,
          surface: 'book-detail',
          viewport: tc.viewport,
          state: 'cover-echo',
        };
        await writeGeometryReport(tc.name, [check, fadeCheck], meta);
        expect(check.pass, check.message).toBe(true);
        expect(fadeCheck.pass, fadeCheck.message).toBe(true);
      } finally {
        await context.close();
      }
    });
  }
});
