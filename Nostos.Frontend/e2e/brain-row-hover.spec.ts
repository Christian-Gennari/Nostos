/**
 * One hover effect per sidebar row, and the name tail fades into the actions.
 *
 * Regression: the row is a <button>, so the action buttons cannot be nested
 * inside it the way the Library's tree nests them inside a <div role="treeitem">
 * — they are a sibling. The fill was therefore driven by `.index-item:hover`,
 * which only matched while the pointer was over the ROW itself. Moving onto the
 * icons left only `.index-row-shell:hover` matching, and the only thing that
 * painted was the ::after fade layer, so a background appeared over the right
 * third of the row. Two competing hover effects on one row.
 *
 * These assertions pin the fix: the fill must be identical wherever the pointer
 * is on the row, and the name's tail must dissolve rather than hard-cut.
 */
import { expect, test } from '@playwright/test';
import { loadFixture } from './support/fixture';
import { cleanupBrain, seedBrain, type BrainSeed } from './support/brain-fixture';
import { DESKTOP_VIEWPORT, newCapturePage } from './support/visual-capture';

let seed: BrainSeed | null = null;
let context: Awaited<ReturnType<typeof newCapturePage>>['context'] | null = null;

test.beforeAll(async ({ browser }) => {
  const fixture = loadFixture();
  seed = await seedBrain(
    fixture.baseUrl,
    `Row Hover ${Date.now().toString(36)}`,
    ['A note about [[Attention]] and [[Memory]].'],
    ['Attention', 'Memory']
  );
  const page = await newCapturePage(browser, DESKTOP_VIEWPORT);
  context = page.context;
});

test.afterAll(async () => {
  const fixture = loadFixture();
  if (seed) await cleanupBrain(fixture.baseUrl, seed);
});

/** Move the REAL mouse: synthetic MouseEvents do not trigger CSS :hover. */
async function moveMouse(page: import('@playwright/test').Page, pt: [number, number]) {
  const client = await page.context().newCDPSession(page);
  await client.send('Input.dispatchMouseEvent', {
    type: 'mouseMoved',
    x: pt[0],
    y: pt[1],
    buttons: 0,
  });
  await page.waitForTimeout(350);
}

test('the row has ONE hover fill, from the name to the icons', async ({ browser }) => {
  const fixture = loadFixture();
  expect(context, 'shared context').not.toBeNull();
  const page = await context!.newPage();
  try {
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.locator('.index-item').first().waitFor({ timeout: 30_000 });
    await page.waitForTimeout(400);

    const boxes = await page.evaluate(() => {
      const shell = document.querySelector('.index-row-shell') as HTMLElement;
      const name = shell.querySelector('.index-item .name') as HTMLElement;
      const acts = shell.querySelector('.row-actions') as HTMLElement;
      const n = name.getBoundingClientRect();
      const a = acts.getBoundingClientRect();
      const s = shell.getBoundingClientRect();
      return {
        namePt: [Math.round(n.left + 14), Math.round(n.top + n.height / 2)],
        iconPt: [Math.round(a.left + a.width / 2), Math.round(a.top + a.height / 2)],
        awayPt: [Math.round(s.left + s.width / 2), Math.round(s.top - 40)],
      } as Record<string, [number, number]>;
    });

    const read = () =>
      page.evaluate(() => {
        const shell = document.querySelector('.index-row-shell') as HTMLElement;
        const item = shell.querySelector('.index-item') as HTMLElement;
        const name = shell.querySelector('.index-item .name') as HTMLElement;
        const badge = shell.querySelector('.index-item .count') as HTMLElement;
        const acts = shell.querySelector('.row-actions') as HTMLElement;
        const cs = (el: HTMLElement) => getComputedStyle(el);
        return {
          // The row's own fill: this is the single hover effect.
          rowBg: cs(item).backgroundColor,
          // No background may be painted on a pseudo layer of the shell: a
          // non-transparent ::after background is what produced the second,
          // partial hover region.
          shellAfterBg: cs(shell, '::after').backgroundImage,
          badgeOpacity: Number(cs(badge).opacity),
          actionsVisibility: cs(acts).visibility,
          nameMasked: (cs(name).maskImage || 'none') !== 'none',
        };
      });

    // --- at rest -----------------------------------------------------------
    await moveMouse(page, boxes.awayPt);
    const rest = await read();
    expect(rest.rowBg, 'row is transparent at rest').toBe('rgba(0, 0, 0, 0)');
    expect(rest.actionsVisibility, 'actions hidden at rest').toBe('hidden');
    expect(rest.nameMasked, 'name is not masked at rest').toBe(false);

    // --- pointer over the NAME --------------------------------------------
    await moveMouse(page, boxes.namePt);
    const onName = await read();

    // --- pointer over the ICONS -------------------------------------------
    await moveMouse(page, boxes.awayPt);
    await moveMouse(page, boxes.iconPt);
    const onIcons = await read();

    console.log('REST     :', JSON.stringify(rest));
    console.log('ON NAME  :', JSON.stringify(onName));
    console.log('ON ICONS :', JSON.stringify(onIcons));

    // THE core assertion: the hover fill must not depend on which part of the
    // row the pointer is over.
    expect(onName.rowBg, 'hovering the name fills the row').not.toBe('rgba(0, 0, 0, 0)');
    expect(
      onIcons.rowBg,
      `hover fill must be identical over the name and over the icons ` +
        `(name: ${onName.rowBg}, icons: ${onIcons.rowBg})`
    ).toBe(onName.rowBg);

    // Everything else the hover reveals must also match, or the two zones would
    // still look different even with a matching fill.
    expect(onIcons.badgeOpacity, 'badge yields in both zones').toBe(onName.badgeOpacity);
    expect(onIcons.actionsVisibility, 'actions show in both zones').toBe(onName.actionsVisibility);
    expect(onIcons.nameMasked, 'name fades in both zones').toBe(onName.nameMasked);
    expect(onName.nameMasked, 'the name tail is masked on hover').toBe(true);
    expect(onName.badgeOpacity, 'badge yields to the actions').toBe(0);
    expect(onName.actionsVisibility, 'actions are revealed').toBe('visible');

    // There must be no second background layer painting a partial region of the
    // row. A gradient here is exactly the competing hover effect.
    expect(
      onName.shellAfterBg,
      `no background may be painted on the shell's ::after layer (got ${onName.shellAfterBg})`
    ).not.toContain('gradient');
  } finally {
    await page.close();
  }
});

test('a selected note keeps its selection fill through focus and hover in light mode', async () => {
  const fixture = loadFixture();
  expect(context, 'shared context').not.toBeNull();
  const page = await context!.newPage();
  try {
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-theme', 'light');
      try {
        localStorage.setItem('nostos.theme', 'light');
      } catch {
        /* ignore */
      }
    });

    await page.getByRole('button', { name: 'Notes', exact: true }).click();
    const row = page.locator('.note-row-item').first();
    await row.waitFor({ timeout: 30_000 });
    await row.click();
    await expect(row).toHaveClass(/active/);

    const read = () =>
      page.evaluate(() => {
        const active = document.querySelector('.note-row-item.active') as HTMLElement | null;
        if (!active) return null;

        const probe = document.createElement('span');
        probe.style.background = 'var(--selection-surface)';
        probe.style.color = 'var(--selection-ink)';
        document.body.appendChild(probe);
        const expectedBg = getComputedStyle(probe).backgroundColor;
        const expectedInk = getComputedStyle(probe).color;
        probe.remove();

        const snippet = active.querySelector('.note-row-snippet') as HTMLElement | null;
        const book = active.querySelector('.note-row-book') as HTMLElement | null;
        return {
          bg: getComputedStyle(active).backgroundColor,
          ink: getComputedStyle(active).color,
          snippetInk: snippet ? getComputedStyle(snippet).color : null,
          bookInk: book ? getComputedStyle(book).color : null,
          expectedBg,
          expectedInk,
        };
      });

    // Click leaves the row focused. Focus-within must not demote the selected
    // surface to the pale hover fill.
    const focusedInitial = await read();
    expect(focusedInitial, 'selected note exists after click').not.toBeNull();
    // The row animates from transparent to its selected fill. Wait for the
    // computed style to reach the token value before asserting the ink pair.
    await expect.poll(async () => (await read())?.bg, {
      message: 'focused selected row reaches selection fill',
    }).toBe(focusedInitial!.expectedBg);
    const focusedAfterClick = await read();
    expect(focusedAfterClick!.bg, 'focused selected row keeps selection fill').toBe(
      focusedAfterClick!.expectedBg
    );
    expect(focusedAfterClick!.ink, 'focused selected row keeps selection ink').toBe(
      focusedAfterClick!.expectedInk
    );

    await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await moveMouse(page, [Math.round(DESKTOP_VIEWPORT.width - 40), 40]);
    const atRest = await read();
    expect(atRest!.bg, 'selected row at rest uses selection fill').toBe(atRest!.expectedBg);

    const rowPt = await row.evaluate((el) => {
      const r = (el as HTMLElement).getBoundingClientRect();
      return [Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2)] as [
        number,
        number,
      ];
    });
    await moveMouse(page, rowPt);
    const hovered = await read();
    expect(hovered!.bg, 'hover must not replace a selected row fill').toBe(hovered!.expectedBg);
    expect(hovered!.ink, 'hover must not replace selected row ink').toBe(hovered!.expectedInk);
    expect(hovered!.snippetInk, 'selected snippet ink is stable on hover').toBe(
      atRest!.snippetInk
    );
    expect(hovered!.bookInk, 'selected source ink is stable on hover').toBe(atRest!.bookInk);

    await moveMouse(page, [Math.round(DESKTOP_VIEWPORT.width - 40), 40]);
    await row.focus();
    const keyboardFocused = await read();
    expect(keyboardFocused!.bg, 'keyboard focus must not replace a selected row fill').toBe(
      keyboardFocused!.expectedBg
    );
  } finally {
    await page.close();
  }
});

test('a long name dissolves into the actions instead of hard-cutting', async ({ browser }) => {
  const fixture = loadFixture();
  expect(context, 'shared context').not.toBeNull();
  const page = await context!.newPage();
  try {
    await page.goto(`${fixture.baseUrl}/second-brain`, { waitUntil: 'domcontentloaded' });
    // Earlier tests share this context and may leave Notes selected. Make the
    // Topics precondition explicit before measuring its index row.
    await page.getByRole('button', { name: 'Topics', exact: true }).click();
    await page.locator('.index-item').first().waitFor({ timeout: 30_000 });

    // A name long enough to run into the actions slot — the case the fade is for.
    await page.evaluate(() => {
      const n = document.querySelector('.index-item .name') as HTMLElement;
      n.textContent = 'A Very Long Topic Name That Overflows The Rail';
    });
    await page.waitForTimeout(200);

    const geom = await page.evaluate(() => {
      const name = document.querySelector('.index-item .name') as HTMLElement;
      const acts = document.querySelector('.row-actions') as HTMLElement;
      const n = name.getBoundingClientRect();
      const a = acts.getBoundingClientRect();
      return {
        nameLeft: Math.round(n.left),
        nameRight: Math.round(n.right),
        actionsLeft: Math.round(a.left),
        width: Math.round(n.width),
      };
    });

    // The name must actually run PAST the actions' left edge, or the fade has
    // nothing to do. This overlap is precisely why the mask cannot be anchored to
    // the name's own right edge: the buttons begin inside the name box, so an
    // edge-anchored fade leaves opaque glyphs painted over by the icons.
    expect(
      geom.nameRight,
      `the name must run under the actions (name right ${geom.nameRight} vs actions left ${geom.actionsLeft})`
    ).toBeGreaterThan(geom.actionsLeft);

    // The mask only exists WHILE hovered, so hover first — reading it at rest
    // returns 'none' and would fail for the wrong reason.
    const namePt = await page.evaluate(() => {
      const n = document.querySelector('.index-item .name') as HTMLElement;
      const b = n.getBoundingClientRect();
      return [Math.round(b.left + 14), Math.round(b.top + b.height / 2)] as [number, number];
    });
    await moveMouse(page, namePt);

    // The mask must be expressed relative to the ACTIONS overlay, not to the
    // name's own right edge. That distinction is the whole fix: the name stretches
    // to within ~28px of the row's right edge, but the buttons begin a full 76px
    // from it, so they start ~48px INSIDE the name box. A fade anchored to the
    // name's own edge therefore leaves ~48px of fully-opaque glyphs underneath the
    // icons — the text is painted over instead of receding.
    const mask = await page.evaluate(() => {
      const name = document.querySelector('.index-item .name') as HTMLElement;
      const cs = getComputedStyle(name);
      return cs.maskImage !== 'none' ? cs.maskImage : (cs as any).webkitMaskImage;
    });

    expect(mask, 'mask exists while hovered').not.toBe('none');
    expect(mask, 'mask stops are percentages of the name box').toContain('100%');

    // Assert the RESOLVED geometry, not the literal `var()` text: getComputedStyle
    // substitutes custom properties, so the rule reads e.g.
    // `calc(100% - 74px) .. calc(100% - 46px)`.
    //
    // The fade must finish at or before the actions' left edge, measured from the
    // name box. Anchoring it to the name's own edge instead would put the fully
    // transparent stop past the buttons, leaving opaque glyphs underneath them —
    // which is the bug this pins.
    const fadeStartPx = await page.evaluate(() => {
      const name = document.querySelector('.index-item .name') as HTMLElement;
      const raw = getComputedStyle(name).maskImage;
      // Pull the first `calc(100% - <n>px)` offset, i.e. how far LEFT of the
      // name's right edge the fade begins.
      const m = /calc\(100% - ([0-9.]+)px\)/.exec(raw);
      return m ? parseFloat(m[1]) : -1;
    });
    expect(fadeStartPx, `fade start offset parsed from "${mask}"`).toBeGreaterThan(0);

    const gapToActions = geom.nameRight - geom.actionsLeft;
    expect(
      fadeStartPx,
      `the fade must begin on the name's side of the actions, and finish at them ` +
        `(fade starts ${fadeStartPx}px left of the name's edge; buttons begin ${gapToActions}px inside it)`
    ).toBeGreaterThanOrEqual(gapToActions);
  } finally {
    await page.close();
  }
});
