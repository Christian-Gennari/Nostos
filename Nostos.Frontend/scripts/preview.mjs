#!/usr/bin/env node
/**
 * preview.mjs — look at a built frontend without deploying it.
 *
 * Why this exists: seeing a UI change on the live Cloud app costs a pin bump, an
 * image and a deploy. This serves the production build from `dist/` instead, as
 * either deployment, so a change can be clicked through or screenshotted from the
 * branch it was made on.
 *
 *   npm run build
 *   npm run preview -- serve [--cloud] [--port 4310] [--host 127.0.0.1]
 *   npm run preview -- shots --out <dir> --paths /settings,/library [--cloud]
 *                            [--widths 1280,390] [--themes light,dark]
 *
 * It cannot change data. Every non-GET request is refused with 405. GET /api/**
 * is forwarded to a running Nostos server (NOSTOS_PREVIEW_API, default
 * http://127.0.0.1:5214), so the pages show real content.
 *
 * `--cloud` answers the capability request and the Cloud-only endpoints with the
 * fixtures below, because the bundle is the same for both deployments and only
 * the server's answers differ. That is a simulation: sign-in, billing and managed
 * storage are not exercised, and only a real Cloud deployment proves them.
 */
import http from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const DIST = join(ROOT, 'dist', 'Nostos.Frontend', 'browser');
const API = new URL(process.env.NOSTOS_PREVIEW_API ?? 'http://127.0.0.1:5214');

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

/** What a Cloud host answers, keyed by exact path. Everything else is forwarded. */
const CLOUD_FIXTURES = {
  '/api/runtime/capabilities': {
    deploymentMode: 'Cloud',
    requiresAuthentication: true,
    canConfigureAiProvider: false,
    managedAi: true,
    managedVoiceTranscription: true,
    usesCloudStorage: true,
    supportsLocalBackupConfiguration: false,
    supportsPrivateNetworkAccess: false,
    supportsEreaderAccess: true,
    usageMeteringAvailable: true,
    accountManagementUrl: 'https://nostos.page/account',
    feedbackUrl: null,
    supportsLibraryMigration: false,
    supportsSafeActivation: false,
    supportsManagedBackups: true,
    supportsManagedBackupRestore: true,
    supportsAccountDeletion: true,
  },
  '/api/auth/session': {
    authenticated: true,
    accountState: 'Active',
    account: { id: 'preview', displayName: 'Reader', email: 'reader@example.test' },
  },
  '/api/cloud/onboarding': {
    state: 'ready',
    subscriptionStatus: 'Active',
    ready: true,
    canCheckout: false,
    canCheckSubscription: false,
    canManageSubscription: false,
    canRetry: false,
    selectedOffer: null,
  },
  '/api/cloud/account/deletion': {
    state: 'Active',
    gracePeriodDays: 14,
    requestedAtUtc: null,
    eligibleAtUtc: null,
    completedAtUtc: null,
    canCancel: false,
    portableExportUrl: null,
  },
  '/api/cloud/ai/usage': {
    state: 'near_limit',
    renewsAtUtc: '2026-11-01T00:00:00Z',
    refill: { available: true, state: 'active' },
  },
  '/api/opds/info': {
    enabled: true,
    catalogUrl: 'https://app.nostos.page/opds/v1.2/catalog',
    urlSource: 'configured',
    localOnly: false,
  },
  '/api/cloud/opds/': {
    enabled: true,
    username: 'reader-preview',
    password: null,
    createdAtUtc: '2026-09-01T00:00:00Z',
    rotatedAtUtc: null,
    revokedAtUtc: null,
  },
  '/api/managed-backups': {
    retentionDays: 14,
    backups: [
      { id: 'preview-2', createdAtUtc: '2026-10-12T02:00:00Z', archiveBytes: 5e6, mediaBytes: 9e8, state: 'completed' },
      { id: 'preview-1', createdAtUtc: '2026-10-11T02:00:00Z', archiveBytes: 5e6, mediaBytes: 9e8, state: 'completed' },
    ],
  },
  '/api/assistant/status': { available: true },
};

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = {};
  for (let i = 0; i < rest.length; i += 1) {
    const name = rest[i];
    if (!name.startsWith('--')) throw new Error(`Unexpected argument: ${name}`);
    const next = rest[i + 1];
    if (next === undefined || next.startsWith('--')) options[name.slice(2)] = true;
    else {
      options[name.slice(2)] = next;
      i += 1;
    }
  }
  return { command, options };
}

function startServer({ cloud, port, host }) {
  if (!existsSync(join(DIST, 'index.html'))) {
    throw new Error(`No build at ${DIST}. Run \`npm run build\` first.`);
  }

  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://preview');

    if (url.pathname.startsWith('/api/')) {
      if (req.method !== 'GET') {
        res.writeHead(405, { 'content-type': 'text/plain' }).end('The preview is read-only.');
        return;
      }
      const fixture = cloud ? CLOUD_FIXTURES[url.pathname] : undefined;
      if (fixture) {
        res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(fixture));
        return;
      }
      http
        .get(
          { host: API.hostname, port: API.port, path: req.url, headers: { accept: 'application/json' } },
          (upstream) => {
            res.writeHead(upstream.statusCode ?? 502, {
              'content-type': upstream.headers['content-type'] ?? 'application/json',
            });
            upstream.pipe(res);
          },
        )
        .on('error', () => res.writeHead(502).end());
      return;
    }

    let file = join(DIST, decodeURIComponent(url.pathname));
    if (!file.startsWith(DIST) || !existsSync(file) || statSync(file).isDirectory()) {
      file = join(DIST, 'index.html');
    }
    res.writeHead(200, {
      'content-type': CONTENT_TYPES[extname(file)] ?? 'application/octet-stream',
      'cache-control': 'no-store',
    });
    res.end(readFileSync(file));
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, () => resolve(server));
  });
}

function list(value, fallback) {
  return (typeof value === 'string' ? value : fallback).split(',').filter(Boolean);
}

async function shots(options) {
  if (typeof options.out !== 'string') throw new Error('shots needs --out <dir>.');
  const paths = list(options.paths, '/library');
  const widths = list(options.widths, '1280,390').map(Number);
  const themes = list(options.themes, 'light');
  const cloud = options.cloud === true;
  mkdirSync(options.out, { recursive: true });

  const server = await startServer({ cloud, port: 0, host: '127.0.0.1' });
  const base = `http://127.0.0.1:${server.address().port}`;
  const { chromium } = await import('@playwright/test');
  const browser = await chromium.launch();
  let failed = false;

  try {
    for (const path of paths) {
      for (const width of widths) {
        for (const theme of themes) {
          const context = await browser.newContext({
            viewport: { width, height: width < 600 ? 844 : 900 },
            deviceScaleFactor: 1,
            serviceWorkers: 'block',
          });
          await context.addInitScript((value) => localStorage.setItem('nostos.theme', value), theme);
          const page = await context.newPage();
          const errors = [];
          page.on('pageerror', (error) => errors.push(String(error)));

          await page.goto(base + path, { waitUntil: 'networkidle' });
          await page.waitForTimeout(400);
          const overflows = await page.evaluate(
            () => document.documentElement.scrollWidth > window.innerWidth,
          );

          const slug = path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '') || 'home';
          const name = `${cloud ? 'cloud' : 'selfhosted'}-${slug}-${width}-${theme}.png`;
          await page.screenshot({ path: join(options.out, name), fullPage: true });
          await context.close();

          const problems = [...errors, ...(overflows ? ['page scrolls sideways'] : [])];
          if (problems.length > 0) failed = true;
          console.log(`${problems.length > 0 ? '✘' : '✔'} ${name}${problems.map((p) => `\n    ${p}`).join('')}`);
        }
      }
    }
  } finally {
    await browser.close();
    server.close();
  }

  if (failed) process.exitCode = 1;
}

async function serve(options) {
  const cloud = options.cloud === true;
  const host = typeof options.host === 'string' ? options.host : '127.0.0.1';
  const port = Number(options.port ?? 4310);
  const server = await startServer({ cloud, port, host });
  console.log(
    `Nostos preview (${cloud ? 'Cloud, simulated' : 'SelfHosted'}, read-only): ` +
      `http://${host}:${server.address().port}/  — API reads from ${API.origin}`,
  );
}

const { command, options } = parseArgs(process.argv.slice(2));
if (command === 'serve') await serve(options);
else if (command === 'shots') await shots(options);
else {
  console.error('Usage: preview.mjs serve|shots [--cloud] …  (see the header of this file)');
  process.exitCode = 2;
}
