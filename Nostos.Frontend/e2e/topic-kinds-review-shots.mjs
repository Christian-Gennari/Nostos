#!/usr/bin/env node
/**
 * Screenshots for the topic-kind review fixes (follow-up to #1786 and #1789).
 *
 * The four states a person can see, captured at 1:1 from a production build:
 *   - the phone map, zoomed in, after "Center on selection";
 *   - the map with the selected topic's kind hidden;
 *   - "Select all" over more than the 500-topic bulk limit;
 *   - `/api/topic-kinds` failing, on the map, the kinds page and the picker.
 *
 * It seeds a throwaway backend (e2e/support/launch-fixture.mjs) with two kinds
 * and 501 untyped topics, then serves the build with scripts/preview.mjs once
 * per deployment (SelfHosted, and Cloud simulated — see that script's header).
 *
 *   node e2e/topic-kinds-review-shots.mjs --api http://127.0.0.1:PORT --out DIR
 *
 * `--side before|after` only names the files. The failure state is produced in
 * the browser with `page.route`, so the read-only preview server is untouched.
 */
import { spawn } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';

const args = {};
for (let i = 2; i < process.argv.length; i += 2) args[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
const api = args.api;
const out = args.out;
const side = args.side ?? 'after';
if (!api || !out) {
  console.error('usage: --api <url> --out <dir> [--side before|after]');
  process.exit(2);
}
mkdirSync(out, { recursive: true });

const MARKER = 'Ursula K. Le Guin';
const BULK_TOPICS = 501;

async function get(urlPath) {
  const res = await fetch(`${api}${urlPath}`);
  if (!res.ok) throw new Error(`GET ${urlPath} -> ${res.status}: ${await res.text()}`);
  return res.json();
}

async function post(urlPath, body) {
  const res = await fetch(`${api}${urlPath}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${urlPath} -> ${res.status}: ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

async function put(urlPath, body) {
  const res = await fetch(`${api}${urlPath}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${urlPath} -> ${res.status}: ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

/**
 * Two kinds, a small connected neighbourhood, and more topics than the bulk cap.
 * The bulk topics are Idea-typed, so hiding Person leaves a map that is drawn —
 * which is what the dimming defect hid.
 */
async function ensureSeed() {
  let topics = await get('/api/topics');
  if (topics.some((topic) => topic.name === MARKER)) return readSeed(topics);

  const book = await post('/api/books', {
    type: 'physical',
    title: 'Topic kinds review',
    author: 'Nostos QA',
    categories: 'visual-qa',
  });
  const linked = ['Ursula K. Le Guin', 'Tolkien', 'Le Guin', 'Story', 'Memory', 'Attention', 'Irony'];
  // Several notes, so these topics outrank the bulk ones and the whole map (top
  // 150 by references) actually draws them.
  for (let i = 0; i < 4; i += 1) {
    await post(`/api/books/${book.id}/notes`, {
      content: `${linked.map((name) => `[[${name}]]`).join(' ')} share thought ${i + 1}.`,
    });
  }

  let kinds = await get('/api/topic-kinds');
  if (!kinds.some((kind) => kind.name === 'Person')) {
    await post('/api/topic-kinds', { name: 'Person', colorKey: 'kind-1' });
    await post('/api/topic-kinds', { name: 'Idea', colorKey: 'kind-2' });
    kinds = await get('/api/topic-kinds');
  }
  const person = kinds.find((kind) => kind.name === 'Person');
  const idea = kinds.find((kind) => kind.name === 'Idea');
  topics = await get('/api/topics');
  const id = (name) => topics.find((topic) => topic.name === name)?.id;
  await put('/api/topics/kind', {
    topicIds: [id('Ursula K. Le Guin'), id('Tolkien'), id('Le Guin')].filter(Boolean),
    kindId: person.id,
  });
  await put('/api/topics/kind', {
    topicIds: [id('Story'), id('Memory')].filter(Boolean),
    kindId: idea.id,
  });

  // The bulk cap needs a sorter long enough to pass 500 rows: one note with many
  // links makes them in a single request.
  let extra = 0;
  while (topics.length < BULK_TOPICS) {
    const names = Array.from({ length: 120 }, (_, i) => `Bulk topic ${String(extra + i).padStart(3, '0')}`);
    await post(`/api/books/${book.id}/notes`, {
      content: `Bulk seeding ${extra}. ${names.map((name) => `[[${name}]]`).join(' ')}`,
    });
    extra += names.length;
    topics = await get('/api/topics');
  }
  // Typed in chunks: the bulk endpoint takes 500 ids at a time.
  const bulk = topics.filter((topic) => topic.name.startsWith('Bulk topic')).map((topic) => topic.id);
  for (let i = 0; i < bulk.length; i += 400) {
    await put('/api/topics/kind', { topicIds: bulk.slice(i, i + 400), kindId: idea.id });
  }
  return readSeed(await get('/api/topics'));
}

function readSeed(topics) {
  const byName = new Map(topics.map((topic) => [topic.name, topic]));
  const person = topics.find((topic) => topic.name === MARKER);
  const idea = topics.find((topic) => topic.name === 'Story');
  return {
    personTopicId: person.id,
    personKindId: person.kindId,
    ideaKindId: idea?.kindId ?? null,
    topicCount: topics.length,
    names: [...byName.keys()].slice(0, 8),
  };
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

async function startPreview(cloud) {
  const port = await freePort();
  const child = spawn(
    process.execPath,
    ['scripts/preview.mjs', 'serve', '--port', String(port), ...(cloud ? ['--cloud'] : [])],
    { env: { ...process.env, NOSTOS_PREVIEW_API: api }, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  child.stderr.on('data', (chunk) => process.stderr.write(chunk));
  await new Promise((resolve) => {
    child.stdout.on('data', (chunk) => {
      process.stdout.write(chunk);
      if (String(chunk).includes('http://')) resolve();
    });
  });
  return { child, base: `http://127.0.0.1:${port}` };
}

/** Where the focused node really is, and where the visible band's middle is. */
async function measureCentre(page) {
  return page.evaluate(() => {
    const sigma = globalThis.__nostosSigma;
    const graph = globalThis.__nostosGraph;
    const focus = new URLSearchParams(location.search).get('focus');
    if (!graph.hasNode(focus)) return null;
    const attrs = graph.getNodeAttributes(focus);
    const point = sigma.graphToViewport({ x: Number(attrs.x), y: Number(attrs.y) });
    const stage = document.querySelector('.map-stage').getBoundingClientRect();
    const legend = document.querySelector('.map-legend')?.getBoundingClientRect();
    const hud = document.querySelector('.map-hud').getBoundingClientRect();
    const top = legend && legend.height ? legend.bottom - stage.top : 0;
    const bottom = hud.height ? stage.bottom - hud.top : 0;
    return {
      nodeY: Math.round(point.y),
      nodeX: Math.round(point.x),
      bandCentreY: Math.round((top + (stage.height - bottom)) / 2),
      stage: { width: Math.round(stage.width), height: Math.round(stage.height) },
      ratio: Number(sigma.getCamera().getState().ratio.toFixed(3)),
    };
  });
}

async function shot(page, file, options = {}) {
  await page.screenshot({ path: path.join(out, `${side}-${file}.png`), ...options });
}

async function capture(browser, base, cloud, width, theme, seed) {
  const viewport = { width, height: width < 600 ? 844 : 900 };
  const context = await browser.newContext({ viewport, deviceScaleFactor: 1, serviceWorkers: 'block' });
  await context.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
  const page = await context.newPage();
  const slug = `${cloud ? 'cloud' : 'selfhosted'}-${width}-${theme}`;
  const failures = [];
  page.on('pageerror', (error) => failures.push(String(error)));

  // 1. The phone map, zoomed in, after "Center on selection".
  if (width < 600) {
    await page.goto(`${base}/brain/map?focus=${seed.personTopicId}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    for (let i = 0; i < 2; i += 1) {
      await page.locator('.map-action[aria-label="Zoom in"]').click();
      await page.waitForTimeout(400);
    }
    await page.locator('.map-action[aria-label="Center on selection"]').click();
    await page.waitForTimeout(900);
    const measured = await measureCentre(page);
    if (!measured) {
      console.log(`${side} ${slug} phone-centred: the focused topic is not drawn`);
      failures.push('focus not drawn');
    } else {
      console.log(
        `${side} ${slug} phone-centred: nodeY=${measured.nodeY} bandCentreY=${measured.bandCentreY} ` +
          `ratio=${measured.ratio}`,
      );
      await page.locator('.map-stage').screenshot({
        path: path.join(out, `${side}-${slug}-map-phone-centred.png`),
      });
    }
  }

  // 2. The selected topic's kind hidden: the map must keep its colours.
  await page.goto(
    `${base}/brain/map?focus=${seed.personTopicId}&kinds=${seed.ideaKindId},none`,
    { waitUntil: 'networkidle' },
  );
  await page.waitForTimeout(1500);
  await page.locator('.map-stage').screenshot({ path: path.join(out, `${side}-${slug}-map-hidden-kind.png`) });

  // 3. Select all over more than the bulk cap.
  await page.goto(`${base}/brain/kinds`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.locator('.sorter-tools button').first().click();
  await page.waitForTimeout(300);
  const selectAllLabel = (await page.locator('.sorter-tools button').first().textContent())?.trim();
  const selectionCount = await page.locator('.selection-bar, nostos-selection-bar').first().textContent();
  console.log(`${side} ${slug} select-all label="${selectAllLabel}" bar="${String(selectionCount).replace(/\s+/g, ' ').trim()}"`);
  await shot(page, `${slug}-kinds-select-all.png`, { fullPage: true });

  // 4. `/api/topic-kinds` failing: the map, the kinds page and the picker.
  await page.route('**/api/topic-kinds', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"unavailable"}' }),
  );
  await page.goto(`${base}/brain/map?focus=${seed.personTopicId}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);
  const colourMode = await page.evaluate(() => {
    const options = [...document.querySelectorAll('.map-controls nostos-segmented button[role="radio"]')];
    const checked = options.find((button) => button.getAttribute('aria-checked') === 'true');
    return { checked: checked?.textContent?.trim() ?? null, cardPill: !!document.querySelector('.map-card-kind-pill') };
  });
  console.log(`${side} ${slug} failed-kinds map: colour="${colourMode.checked}" cardPill=${colourMode.cardPill}`);
  // The whole place, so the Colour control and the card are in the same frame.
  await page.locator('.brain-map').screenshot({ path: path.join(out, `${side}-${slug}-map-failed-kinds.png`) });

  await page.goto(`${base}/brain/kinds`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  const kindsError = await page.evaluate(() => ({
    error: !!document.querySelector('.kinds-failed'),
    newKindDisabled: document.querySelector('.new-kind')?.disabled ?? null,
  }));
  console.log(`${side} ${slug} failed-kinds page: error=${kindsError.error} newKindDisabled=${kindsError.newKindDisabled}`);
  await shot(page, `${slug}-kinds-failed.png`, { fullPage: true });

  await page.goto(`${base}/brain/topics/${seed.personTopicId}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800);
  await page.locator('.kind-trigger').click();
  await page.waitForTimeout(500);
  const pickerError = await page.evaluate(() => ({
    error: !!document.querySelector('.kind-picker-failed'),
    newKind: !!document.querySelector('.kind-picker-action'),
  }));
  console.log(`${side} ${slug} failed-kinds picker: error=${pickerError.error} newKind=${pickerError.newKind}`);
  await shot(page, `${slug}-picker-failed.png`);

  await context.close();
  if (failures.length) console.log(`${side} ${slug} page errors: ${failures.join(' | ')}`);
}

const { chromium } = await import('@playwright/test');
const seed = await ensureSeed();
console.log(`${side} seed: ${seed.topicCount} topics, Person=${seed.personKindId}, Idea=${seed.ideaKindId}`);
const browser = await chromium.launch();
const previews = [];
try {
  for (const cloud of [false, true]) {
    const preview = await startPreview(cloud);
    previews.push(preview);
    for (const width of [1280, 390]) {
      for (const theme of ['light', 'dark']) {
        await capture(browser, preview.base, cloud, width, theme, seed);
      }
    }
  }
} finally {
  await browser.close();
  for (const preview of previews) preview.child.kill();
}
