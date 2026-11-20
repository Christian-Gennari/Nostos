#!/usr/bin/env node
/**
 * Live "use it for real" walk of Ask Nostos (#1584): a throwaway SelfHosted
 * backend on a temp database, a small seeded library, and a metering proxy
 * between the backend and the OpenAI-compatible gateway.
 *
 *   node e2e/support/ask-live-walk.mjs up      # start proxy + backend, seed, print the URL
 *   node e2e/support/ask-live-walk.mjs status  # spend so far
 *   node e2e/support/ask-live-walk.mjs down    # stop both, keep proxy.jsonl and backend.log
 *
 * It is a manual tool, not a *.spec.ts, so CI never runs it. It calls a paid
 * model, so the proxy enforces a hard spend cap and refuses once it is spent.
 *
 * Environment:
 *   WALK_GATEWAY_BASE_URL   gateway base URL including /v1 (default the managed gateway)
 *   WALK_GATEWAY_KEY        the gateway key; held by the proxy only, never logged,
 *                           never given to the backend
 *   WALK_MODEL              model id (default openai/gpt-6-luna)
 *   WALK_MAX_USD            spend cap in USD (default 1)
 *   WALK_PRICE_IN / OUT     USD per million prompt / completion tokens (0.10 / 0.50)
 *   WALK_REASONING_EFFORT   sent with every call, like the quality bed (default none)
 *   WALK_DIR                where state, proxy.jsonl and the temp library live
 *
 * The backend runs with the product defaults (Assistant:MaxToolIterations 12).
 * Needs `dotnet build Nostos.Backend` (Debug) and `npm run build` first.
 */
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';

import {
  DIST_BROWSER,
  E2E_DIR,
  createLogger,
  freePort,
  spawnBackend,
  stopProcess,
  waitForHealthy,
} from './host-launcher.mjs';

const log = createLogger('walk');
const DIR = process.env.WALK_DIR ?? path.join(tmpdir(), 'nostos-ask-live-walk');
const STATE = path.join(DIR, 'state.json');
const PROXY_LOG = path.join(DIR, 'proxy.jsonl');
const ASSETS = path.join(E2E_DIR, 'assets');

const num = (value, fallback) => (Number.isFinite(Number(value)) && value !== undefined ? Number(value) : fallback);
const cfg = {
  base: (process.env.WALK_GATEWAY_BASE_URL ?? 'https://ai-gateway.vercel.sh/v1').replace(/\/$/, ''),
  model: process.env.WALK_MODEL ?? 'openai/gpt-6-luna',
  cap: num(process.env.WALK_MAX_USD, 1),
  priceIn: num(process.env.WALK_PRICE_IN, 0.1),
  priceOut: num(process.env.WALK_PRICE_OUT, 0.5),
  effort: process.env.WALK_REASONING_EFFORT ?? 'none',
};

// ---------------------------------------------------------------------------
// Metering proxy (runs inside the `serve-proxy` child process)
// ---------------------------------------------------------------------------

async function serveProxy(port) {
  const key = process.env.WALK_GATEWAY_KEY;
  if (!key) throw new Error('WALK_GATEWAY_KEY is not set');
  let promptTokens = 0;
  let completionTokens = 0;
  const spent = () => (promptTokens * cfg.priceIn + completionTokens * cfg.priceOut) / 1e6;

  const server = http.createServer(async (req, res) => {
    const send = (code, body) => {
      res.writeHead(code, { 'content-type': 'application/json' });
      res.end(JSON.stringify(body));
    };
    if (req.url === '/spend') return send(200, { spentUsd: spent(), promptTokens, completionTokens, capUsd: cfg.cap });
    if (req.method !== 'POST' || !req.url.endsWith('/chat/completions'))
      return send(404, { error: { message: 'only chat completions are proxied' } });
    if (spent() >= cfg.cap)
      return send(429, { error: { message: `walk spend cap of $${cfg.cap} reached` } });

    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    let body;
    try {
      body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    } catch {
      return send(400, { error: { message: 'invalid json' } });
    }
    if (cfg.effort && cfg.effort !== 'none-omit') body.reasoning_effort = cfg.effort;

    const started = Date.now();
    let upstream;
    try {
      upstream = await fetch(`${cfg.base}/chat/completions`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', authorization: `Bearer ${key}` },
        body: JSON.stringify(body),
      });
    } catch (error) {
      return send(502, { error: { message: `gateway unreachable: ${error.message}` } });
    }
    const text = await upstream.text();
    let usage = null;
    let tools = [];
    let finish = null;
    try {
      const parsed = JSON.parse(text);
      usage = parsed.usage ?? null;
      tools = (parsed.choices?.[0]?.message?.tool_calls ?? []).map((call) => call.function?.name);
      finish = parsed.choices?.[0]?.finish_reason ?? null;
    } catch {
      // not JSON: logged below with no usage
    }
    promptTokens += usage?.prompt_tokens ?? 0;
    completionTokens += usage?.completion_tokens ?? 0;
    appendFileSync(
      PROXY_LOG,
      JSON.stringify({
        at: new Date().toISOString(),
        status: upstream.status,
        model: body.model,
        durationMs: Date.now() - started,
        messages: body.messages?.length ?? 0,
        promptTokens: usage?.prompt_tokens ?? null,
        completionTokens: usage?.completion_tokens ?? null,
        tools,
        finish,
        spentUsd: Number(spent().toFixed(6)),
      }) + '\n',
    );
    res.writeHead(upstream.status, { 'content-type': upstream.headers.get('content-type') ?? 'application/json' });
    res.end(text);
  });
  server.listen(port, '127.0.0.1', () => log(`proxy on ${port}, cap $${cfg.cap}`));
}

// ---------------------------------------------------------------------------
// Seeding
// ---------------------------------------------------------------------------

async function api(baseUrl, method, urlPath, body) {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${urlPath} -> ${res.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}

async function upload(baseUrl, bookId, file, type) {
  const form = new FormData();
  form.append('file', new Blob([readFileSync(path.join(ASSETS, file))], { type }), file);
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`upload ${file} -> ${res.status}`);
}

const idOf = (created) => created?.book?.id ?? created?.bookId ?? created?.id;

async function seed(baseUrl) {
  const book = async (type, title, author) => idOf(await api(baseUrl, 'POST', '/api/books/', { type, title, author }));
  const note = (bookId, content, extra = {}) =>
    api(baseUrl, 'POST', `/api/books/${bookId}/notes`, { content, ...extra });
  const page = (n) => ({ sourceAnchorKind: 'physical_page', sourceAnchorValue: String(n), anchorVerified: false });

  const dracula = await book('ebook', 'Dracula', 'Bram Stoker');
  await upload(baseUrl, dracula, 'reader-margins.epub', 'application/epub+zip');
  const iliad = await book('ebook', 'The Iliad', 'Homer');
  await upload(baseUrl, iliad, 'reader-notes.pdf', 'application/pdf');
  const seeing = await book('physical', 'Ways of Seeing', 'John Berger');
  const time = await book('physical', 'The Order of Time', 'Carlo Rovelli');
  const room = await book('physical', "A Room of One's Own", 'Virginia Woolf');
  const walden = await book('physical', 'Walden', 'Henry David Thoreau');
  const bleak = await book('physical', 'The Left Hand of Darkness', 'Ursula K. Le Guin');
  const dispossessed = await book('physical', 'The Dispossessed', 'Ursula K. Le Guin');

  await note(seeing, 'Seeing comes before words. What I look at is shaped by what I attend to. [[Attention]]', page(7));
  await note(room, 'A room, money and a lock on the door: the conditions for writing. [[Solitude]]', page(110));
  await note(walden, 'He went to the woods to live deliberately and to attend only to the essential facts of life. [[Attention]] [[Solitude]]', page(88));
  await note(time, 'Time is not one thing: it changes with where you stand. [[Time]]', page(21));
  await note(bleak, 'Loyalty and betrayal across the ice; the story is a record of attention paid to one friend. [[Attention]]', page(204));
  await note(dracula, 'The journal voice makes the strange feel documented. [[Time]]', {
    selectedText: 'Left Munich at 8:35 P. M., on 1st May, arriving at Vienna early next morning',
  });

  const fiction = idOf(await api(baseUrl, 'POST', '/api/collections', { name: 'Fiction' }));
  const essays = idOf(await api(baseUrl, 'POST', '/api/collections', { name: 'Essays' }));
  for (const id of [dracula, iliad, bleak, dispossessed]) await api(baseUrl, 'PUT', `/api/books/${id}/collections`, { collectionIds: [fiction] })
  for (const id of [seeing, time, room, walden]) await api(baseUrl, 'PUT', `/api/books/${id}/collections`, { collectionIds: [essays] })
  return { dracula, iliad, seeing, time, room, walden, bleak, dispossessed, fiction, essays };
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

async function up() {
  if (existsSync(STATE)) throw new Error(`already up (${STATE}); run down first`);
  mkdirSync(DIR, { recursive: true });
  const wwwroot = path.join(DIR, 'wwwroot');
  cpSync(DIST_BROWSER, wwwroot, { recursive: true });

  const proxyPort = await freePort();
  const { spawn } = await import('node:child_process');
  const proxy = spawn(process.execPath, [new URL(import.meta.url).pathname, 'serve-proxy', String(proxyPort)], {
    env: { ...process.env, WALK_DIR: DIR },
    stdio: 'ignore',
    detached: true,
  });
  proxy.unref();

  const port = await freePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const child = spawnBackend({
    tempDir: DIR,
    port,
    log,
    env: {
      Assistant__Enabled: 'true',
      Assistant__BaseUrl: `http://127.0.0.1:${proxyPort}/v1`,
      Assistant__Model: cfg.model,
      Assistant__ApiKeyEnvironmentVariable: 'NOSTOS_WALK_KEY',
      NOSTOS_WALK_KEY: 'walk-proxy-holds-the-real-key',
      WALK_GATEWAY_KEY: '',
      DOTNET_hostBuilder__reloadConfigOnChange: 'false',
    },
  });
  writeFileSync(STATE, JSON.stringify({ baseUrl, port, proxyPort, backendPid: child.pid, proxyPid: proxy.pid, dir: DIR }, null, 2));
  await waitForHealthy(baseUrl, { timeoutMs: 240_000 });
  const ids = await seed(baseUrl);
  writeFileSync(STATE, JSON.stringify({ baseUrl, port, proxyPort, backendPid: child.pid, proxyPid: proxy.pid, dir: DIR, ids }, null, 2));
  console.log(JSON.stringify({ baseUrl, ids }, null, 2));
}

async function status() {
  const state = JSON.parse(readFileSync(STATE, 'utf8'));
  const spend = await (await fetch(`http://127.0.0.1:${state.proxyPort}/spend`)).json();
  console.log(JSON.stringify({ baseUrl: state.baseUrl, ...spend }, null, 2));
}

async function down() {
  if (!existsSync(STATE)) return log('not up');
  const state = JSON.parse(readFileSync(STATE, 'utf8'));
  let spend = null;
  try {
    spend = await (await fetch(`http://127.0.0.1:${state.proxyPort}/spend`)).json();
    writeFileSync(path.join(DIR, 'spend.json'), JSON.stringify(spend, null, 2));
  } catch {
    // proxy already gone
  }
  await stopProcess(state.backendPid, { log });
  await stopProcess(state.proxyPid, { log });
  const { rmSync } = await import('node:fs');
  rmSync(STATE, { force: true });
  console.log(JSON.stringify({ stopped: true, spend }, null, 2));
}

const [command, arg] = process.argv.slice(2);
const commands = { up, status, down, 'serve-proxy': () => serveProxy(Number(arg)) };
if (!commands[command]) {
  console.error('usage: ask-live-walk.mjs up|status|down');
  process.exit(2);
}
await commands[command]();
