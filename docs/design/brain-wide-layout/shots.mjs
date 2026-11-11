#!/usr/bin/env node
/**
 * Screenshot the static mockups at exact viewport widths. One browser, one
 * page at a time (the load guard's rule: never several heavy Chromium jobs).
 *
 *   node shots.mjs [--out shots]
 *
 * Serves this folder over HTTP because Chromium will not load woff2 fonts from
 * file:// in every configuration; then screenshots each page at 1440 and 1920,
 * light and dark, plus the closed-sidebar variant of A.
 *
 * It also fails loudly if a page scrolls sideways, if a font did not load, or
 * if an image is missing — the same checks the app's preview `shots` makes.
 */
import http from 'node:http';
import { readFile, mkdir, stat } from 'node:fs/promises';
import { extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

// This folder is outside Nostos.Frontend, so resolve Playwright from the
// frontend's node_modules (the app's own copy) rather than adding a package here.
const require = createRequire(import.meta.url);
const playwrightPath = process.env.PLAYWRIGHT_MODULE ?? '../../../Nostos.Frontend/node_modules/@playwright/test';
const { chromium } = require(playwrightPath);

const ROOT = resolve(fileURLToPath(new URL('.', import.meta.url)));
const outIndex = process.argv.indexOf('--out');
const OUT = outIndex > -1 ? resolve(process.argv[outIndex + 1]) : join(ROOT, 'shots');

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.woff2': 'font/woff2',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
};

function serve() {
  const server = http.createServer(async (req, res) => {
    try {
      const path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
      const file = join(ROOT, path === '/' ? 'README.md' : path);
      if (!file.startsWith(ROOT)) throw new Error('outside root');
      const info = await stat(file);
      if (!info.isFile()) throw new Error('not a file');
      res.writeHead(200, { 'content-type': TYPES[extname(file)] ?? 'application/octet-stream' });
      res.end(await readFile(file));
    } catch {
      res.writeHead(404).end('not found');
    }
  });
  return new Promise((ok) => server.listen(0, '127.0.0.1', () => ok(server)));
}

const jobs = [
  ['a-list-detail.html', 'a-list-detail', 1440, 'light', ''],
  ['a-list-detail.html', 'a-list-detail', 1440, 'dark', ''],
  ['a-list-detail.html', 'a-list-detail', 1920, 'light', ''],
  ['a-list-detail.html', 'a-list-detail', 1920, 'dark', ''],
  ['a-list-detail.html', 'a-list-detail', 1920, 'light', '&sidebar=closed', '-sidebar-closed'],
  ['b-context-rail.html', 'b-context-rail', 1440, 'light', ''],
  ['b-context-rail.html', 'b-context-rail', 1440, 'dark', ''],
  ['b-context-rail.html', 'b-context-rail', 1920, 'light', ''],
  ['b-context-rail.html', 'b-context-rail', 1920, 'dark', ''],
  ['c-combined.html', 'c-combined', 1440, 'light', ''],
  ['c-combined.html', 'c-combined', 1440, 'dark', ''],
  ['c-combined.html', 'c-combined', 1920, 'light', ''],
  ['c-combined.html', 'c-combined', 1920, 'dark', ''],
  // The other half of C: selecting a note swaps the place panel for the detail.
  ['c-combined.html', 'c-combined-selected', 1440, 'light', '&note=1'],
  ['c-combined.html', 'c-combined-selected', 1920, 'dark', '&note=1'],
];

await mkdir(OUT, { recursive: true });
const server = await serve();
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();

let failed = false;
try {
  for (const [path, slug, width, theme, extra, suffix = ''] of jobs) {
    const page = await browser.newPage({
      viewport: { width, height: 900 },
      deviceScaleFactor: 1,
    });
    const errors = [];
    page.on('pageerror', (e) => errors.push(String(e)));
    await page.goto(`${base}/${path}?theme=${theme}${extra}`, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(150);

    const checks = await page.evaluate(() => ({
      overflow: document.documentElement.scrollWidth > window.innerWidth,
      fonts: [...document.fonts].filter((f) => f.status === 'error').map((f) => f.family),
      missing: [...document.images].filter((i) => !i.complete || i.naturalWidth === 0).length,
      hanken: document.fonts.check('16px "Hanken Grotesk"'),
      newsreader: document.fonts.check('16px Newsreader'),
    }));
    const name = `${slug}-${width}-${theme}${suffix}.png`;
    await page.screenshot({ path: join(OUT, name), fullPage: false });
    await page.close();

    const problems = [
      ...errors,
      ...(checks.overflow ? ['page scrolls sideways'] : []),
      ...(checks.missing ? [`${checks.missing} image(s) missing`] : []),
      ...(checks.fonts.length ? [`font failed: ${checks.fonts.join(', ')}`] : []),
      ...(!checks.hanken || !checks.newsreader ? ['a face did not load'] : []),
    ];
    if (problems.length) failed = true;
    console.log(`${problems.length ? '✘' : '✔'} ${name}${problems.map((p) => `\n    ${p}`).join('')}`);
  }
} finally {
  await browser.close();
  server.close();
}

if (failed) process.exitCode = 1;
