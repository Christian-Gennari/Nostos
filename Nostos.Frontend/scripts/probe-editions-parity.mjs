#!/usr/bin/env node
/**
 * Editions-modal ↔ book-page parity probe.
 *
 * Why this exists: the "Manage editions" modal is opened FROM the book page's own
 * editions card, and it had drifted off it — a serif uppercase section label where
 * the page used a tracked sans one, a 12.8px/400 row title where the page used
 * 0.88rem/600, square 44px text buttons where every other action in the app is a
 * stadium pill, "you are here" as an uppercase text line where the page fills the
 * row. None of that is visible to `npm run check` (the CSS parses, the tokens have
 * dark counterparts) and none of it fails a unit test: it only shows up as "this
 * dialog looks like a different app", which is exactly what a screenshot review is
 * asked to catch and what no reviewer catches twice.
 *
 * So the recipes are compared to the page's, property by property. Every pair below
 * is a copy that must stay a copy: change the page's edition row and this fails until
 * the modal is re-synced (or the pair is deliberately removed).
 *
 * Runs against a served app with real library data (the modal needs a book whose
 * work has more than one member).
 *
 * Usage: node scripts/probe-editions-parity.mjs [--port 5471] [--book <id>]
 */
import { chromium } from '@playwright/test';

const argv = process.argv.slice(2);
const arg = (n, d) => {
  const i = argv.indexOf(`--${n}`);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};
const BASE = `http://127.0.0.1:${arg('port', '5099')}`;

/**
 * A recipe pair: `modal` must compute the same values as `page` for every listed
 * property. `props` are CSSStyleDeclaration keys, so the names describe themselves.
 */
const PAIRS = [
  {
    name: 'section header',
    modal: '.editions-section-header',
    page: '.edition-section-header',
    props: ['fontFamily', 'fontSize', 'fontWeight', 'letterSpacing', 'textTransform', 'color'],
  },
  {
    name: 'section count badge',
    modal: '.editions-count-badge',
    page: '.edition-count-badge',
    props: [
      'height',
      'borderRadius',
      'backgroundColor',
      'color',
      'fontSize',
      'fontWeight',
      'paddingLeft',
      'paddingRight',
    ],
  },
  {
    name: 'row box',
    modal: '.manage-member',
    page: '.edition-select-card',
    props: [
      'borderRadius',
      'paddingTop',
      'paddingBottom',
      'paddingLeft',
      'paddingRight',
      'borderTopWidth',
    ],
  },
  {
    name: 'current row fill',
    modal: '.manage-member.is-current',
    page: '.edition-select-card.active',
    props: ['backgroundColor', 'borderTopColor', 'color'],
  },
  {
    name: 'row title',
    modal: '.manage-member-title',
    page: '.card-format-title',
    props: ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight'],
  },
  {
    name: 'row sub, current row',
    modal: '.manage-member.is-current .manage-member-author',
    page: '.edition-select-card.active .card-format-sub',
    props: ['fontSize', 'color'],
  },
  {
    name: 'row sub, plain row',
    modal: '.manage-member:not(.is-current) .manage-member-author',
    page: '.edition-select-card:not(.active) .card-format-sub',
    props: ['fontSize', 'color'],
  },
  {
    name: 'you-are-here badge',
    modal: '.manage-member-flag',
    page: '.card-status-pill.current',
    props: ['fontSize', 'fontWeight', 'borderRadius', 'backgroundColor', 'color', 'paddingLeft'],
  },
  {
    name: 'action pill',
    modal: '.manage-member-action',
    page: '.btn.btn-secondary.pill-btn',
    props: ['height', 'borderRadius', 'fontSize', 'fontWeight', 'borderTopColor', 'backgroundColor'],
  },
];

const read = ([sel, props]) => {
  const el = document.querySelector(sel);
  if (!el) return null;
  const cs = getComputedStyle(el);
  return Object.fromEntries(props.map((p) => [p, cs[p]]));
};

/** The first book whose work has more than one member — the modal needs one. */
async function findMultiEditionBook(page) {
  const res = await page.request.get(`${BASE}/api/books?pageSize=100`);
  const body = await res.json();
  const hit = (body.items ?? []).find((b) => (b.editionCount ?? 1) > 1);
  return hit?.id ?? null;
}

const browser = await chromium.launch();
const failures = [];
let compared = 0;
try {
  const ctx = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
  });
  const page = await ctx.newPage();

  const bookId = arg('book', null) ?? (await findMultiEditionBook(page));
  if (!bookId) {
    console.error('✖ no multi-edition book found; pass --book <id>');
    process.exit(1);
  }

  await page.goto(`${BASE}/library/${bookId}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);

  // The page's side of every pair, captured before the modal opens.
  const pageSide = {};
  for (const pair of PAIRS) pageSide[pair.name] = await page.evaluate(read, [pair.page, pair.props]);

  const open = page.locator('.edition-section-action').first();
  if (!(await open.count())) {
    console.error(`✖ ${bookId} renders no "Manage editions" entry point`);
    process.exit(1);
  }
  await open.click();
  await page.waitForSelector('.editions-modal-card');
  await page.waitForTimeout(600);

  for (const pair of PAIRS) {
    const modalSide = await page.evaluate(read, [pair.modal, pair.props]);
    const wanted = pageSide[pair.name];
    if (!modalSide || !wanted) {
      failures.push({
        name: pair.name,
        detail: `missing element — modal ${pair.modal}: ${!!modalSide}, page ${pair.page}: ${!!wanted}`,
      });
      continue;
    }
    compared += 1;
    const diffs = pair.props.filter((p) => modalSide[p] !== wanted[p]);
    if (diffs.length) {
      failures.push({
        name: pair.name,
        detail: diffs.map((p) => `${p}: modal ${modalSide[p]} vs page ${wanted[p]}`).join('; '),
      });
    } else {
      console.log(`  ✔ ${pair.name} (${pair.props.length} properties)`);
    }
  }
  await ctx.close();
} finally {
  await browser.close();
}

if (failures.length) {
  console.error(`\n✖ editions modal has drifted off the book page (${failures.length} of ${compared}):`);
  for (const f of failures) console.error(`   ${f.name}: ${f.detail}`);
  console.error('');
  process.exit(1);
}
console.log(`\n  ✔ editions modal matches the book page's recipes (${compared} pairs)\n`);
