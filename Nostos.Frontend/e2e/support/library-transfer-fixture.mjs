#!/usr/bin/env node
/**
 * Library-transfer (#680 slice B10) browser-QA fixture launcher.
 *
 * Owns one or more fully isolated SelfHosted Nostos instances for real-browser
 * QA of export/import/activation:
 *   - each instance gets its own temp dir (fresh SQLite DB, temp wwwroot with
 *     the freshly built Angular app, backend.log) and a free 127.0.0.1 port;
 *   - the real ASP.NET Core backend serves both the SPA and the migration API;
 *   - the migration capability (`AdvertiseLibraryMigration`) is flipped in the
 *     source tree while this slice runs, so no production switch exists here;
 *   - `Storage__ChunkBytes` is pinned to the 4 MiB contract minimum so an
 *     ~17 MiB synthetic archive crosses the >= 5 chunk requirement of the
 *     assignment; disk safety margins are zeroed for a disposable root;
 *   - libraries are seeded through the app's real REST APIs (books with
 *     generated EPUB/PDF/MP3 files, notes/highlights, collections, reading
 *     progress, Writing Studio documents). The expected facts are written to
 *     the state file so specs assert content instead of guessing.
 *
 * Commands:
 *   launch-all                       build once, start the base instances
 *   launch-instance <name> [a|b|none] start one more instance (default none)
 *   restart-instance <name>          SIGTERM + respawn over the same root
 *   kill-instance <name>             terminate one instance + remove its root
 *   kill-all                         terminate every instance + remove roots
 *
 * State lives in e2e/support/library-transfer-fixture-state.json (no tokens:
 * this fixture never enables MCP).
 */
import { spawn, spawnSync } from 'node:child_process';
import { deflateRawSync } from 'node:zlib';
import {
  closeSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  rmSync,
  writeFileSync,
  copyFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import net from 'node:net';
import crypto from 'node:crypto';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const E2E_DIR = path.resolve(HERE, '..');
const FRONTEND_DIR = path.resolve(E2E_DIR, '..');
const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
const BACKEND_DIR = path.join(REPO_ROOT, 'Nostos.Backend');
const STATE_FILE = path.join(HERE, 'library-transfer-fixture-state.json');
const TEST_RESULTS = path.join(E2E_DIR, 'test-results', 'library-transfer');

const DLL = path.join(BACKEND_DIR, 'bin', 'Debug', 'net10.0', 'Nostos.Backend.dll');
const DIST_BROWSER = path.join(FRONTEND_DIR, 'dist', 'Nostos.Frontend', 'browser');

const CHUNK_SIZE_BYTES = 4 * 1024 * 1024; // contract minimum: >= 5 chunks for a 17 MiB archive
const HEALTH_TIMEOUT_MS = 240_000;

// Only the immutable export source is global. Every mutating scenario
// launches its own fresh destination on demand so browser projects and repeat
// runs never share consumed state.
const BASE_INSTANCES = {
  source: 'a',
};

function log(...args) {
  console.log('[lt-fixture]', ...args);
}

function readState() {
  if (!existsSync(STATE_FILE)) return { instances: {} };
  return JSON.parse(readFileSync(STATE_FILE, 'utf8'));
}

function writeState(state) {
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
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

async function waitForHealthy(baseUrl, timeoutMs = HEALTH_TIMEOUT_MS) {
  const deadline = Date.now() + timeoutMs;
  let lastErr = null;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`${baseUrl}/api/books?pageSize=1`);
      if (res.ok) return;
      lastErr = new Error(`HTTP ${res.status}`);
    } catch (err) {
      lastErr = err;
    }
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  throw new Error(`Backend at ${baseUrl} not healthy within ${timeoutMs}ms (${lastErr})`);
}

async function waitForPortFree(port, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const free = await new Promise((resolve) => {
      const server = net.createServer();
      server.once('error', () => resolve(false));
      server.listen(port, '127.0.0.1', () => server.close(() => resolve(true)));
    });
    if (free) return;
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  throw new Error(`Port ${port} did not free within ${timeoutMs}ms`);
}

function build() {
  if (process.env.E2E_SKIP_BUILDS === '1') {
    for (const [label, p] of [
      ['backend DLL', DLL],
      ['Angular dist index', path.join(DIST_BROWSER, 'index.html')],
    ]) {
      if (!existsSync(p)) throw new Error(`E2E_SKIP_BUILDS=1 but ${label} missing at ${p}`);
    }
    log('E2E_SKIP_BUILDS=1: reusing existing build artifacts');
    return;
  }

  log('building backend (dotnet build -c Debug)...');
  const backend = spawnSync(
    'dotnet',
    ['build', path.join(BACKEND_DIR, 'Nostos.Backend.csproj'), '-c', 'Debug', '--nologo', '-v', 'q'],
    { stdio: 'inherit', timeout: 600_000 },
  );
  if (backend.status !== 0) throw new Error(`dotnet build failed (exit ${backend.status})`);
  if (!existsSync(DLL)) throw new Error(`Backend DLL not found: ${DLL}`);

  log('building Angular frontend (npm run build)...');
  const frontend = spawnSync('npm', ['run', 'build'], {
    cwd: FRONTEND_DIR,
    stdio: 'inherit',
    timeout: 900_000,
  });
  if (frontend.status !== 0) throw new Error(`npm run build failed (exit ${frontend.status})`);
  if (!existsSync(path.join(DIST_BROWSER, 'index.html'))) {
    throw new Error(`Angular build output missing index.html at ${DIST_BROWSER}`);
  }
}

function spawnBackend(instance) {
  const logFd = openSync(path.join(instance.tempDir, 'backend.log'), 'a');
  let child;
  try {
    child = spawn(
      'dotnet',
      [DLL, '--contentRoot', instance.tempDir, '--urls', `http://127.0.0.1:${instance.port}`],
      {
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: 'Production',
          DOTNET_NOLOGO: '1',
          Logging__LogLevel__Default: process.env.LT_LOG_LEVEL ?? 'Warning',
          // 4 MiB chunking: the contract minimum, forcing >= 5 chunks on the
          // ~17.5 MiB synthetic archive. Margins are zeroed for disposable roots.
          Storage__ChunkBytes: String(CHUNK_SIZE_BYTES),
          Storage__MinChunkBytes: String(CHUNK_SIZE_BYTES),
          Storage__MaxChunkBytes: String(CHUNK_SIZE_BYTES),
          Storage__DiskSafetyMarginBytes: '0',
          Storage__DiskSafetyMarginPercent: '0',
          // Make the post-activation derived rebuild prompt so the browser
          // suite can assert the NEW generation is indexed without waiting a
          // production startup delay.
          ActivationMaintenance__StartupDelaySeconds: '1',
          ActivationMaintenance__DerivedRebuildIntervalSeconds: '2',
        },
        stdio: ['ignore', logFd, logFd],
        detached: true,
      },
    );
  } finally {
    closeSync(logFd);
  }
  child.unref();
  child.on('error', (err) => log(`[${instance.name}] spawn error: ${err.message}`));
  return child;
}

function newInstance(name, seed) {
  const tempDir = mkdtempSync(path.join(tmpdir(), `nostos-lt-${name}-`));
  return {
    name,
    seed,
    tempDir,
    port: null,
    baseUrl: null,
    backendPid: null,
    startedAt: null,
  };
}

async function startInstance(instance) {
  const port = await freePort();
  instance.port = port;
  instance.baseUrl = `http://127.0.0.1:${port}`;
  const wwwroot = path.join(instance.tempDir, 'wwwroot');
  if (!existsSync(wwwroot)) {
    const { cpSync } = await import('node:fs');
    cpSync(DIST_BROWSER, wwwroot, { recursive: true });
  }
  const child = spawnBackend(instance);
  instance.backendPid = child.pid;
  instance.startedAt = new Date().toISOString();
  await waitForHealthy(instance.baseUrl);
  return instance;
}

// --- seeding through real APIs ------------------------------------------------

function crc32(buffer) {
  let table = crc32.table;
  if (!table) {
    table = crc32.table = new Int32Array(256);
    for (let n = 0; n < 256; n += 1) {
      let c = n;
      for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      table[n] = c;
    }
  }
  let crc = -1;
  for (let i = 0; i < buffer.length; i += 1) crc = (crc >>> 8) ^ table[(crc ^ buffer[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}

/** Minimal store-only ZIP writer (synthetic EPUB fixtures; no third-party dep). */
function storeZip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8');
    const data = Buffer.isBuffer(entry.data) ? entry.data : Buffer.from(entry.data, 'utf8');
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0, 6);
    local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    locals.push(local, name, data);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(0, 38);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);
    offset += local.length + name.length + data.length;
  }
  const centralSize = centrals.reduce((total, part) => total + part.length, 0);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(0, 4);
  eocd.writeUInt16LE(0, 6);
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(centralSize, 12);
  eocd.writeUInt32LE(offset, 16);
  eocd.writeUInt16LE(0, 20);
  return Buffer.concat([...locals, ...centrals, eocd]);
}

function makeEpub(title, paragraph) {
  return storeZip([
    { name: 'mimetype', data: 'application/epub+zip' },
    {
      name: 'META-INF/container.xml',
      data:
        '<?xml version="1.0" encoding="UTF-8"?>\n' +
        '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n' +
        `<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>\n` +
        '</container>\n',
    },
    {
      name: 'OEBPS/content.opf',
      data:
        '<?xml version="1.0" encoding="UTF-8"?>\n' +
        '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">\n' +
        '  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n' +
        `    <dc:identifier id="bookid">urn:uuid:${crypto.randomUUID()}</dc:identifier>\n` +
        `    <dc:title>${title}</dc:title>\n` +
        '    <dc:language>en</dc:language>\n' +
        '    <dc:creator>Nostos LT Fixture</dc:creator>\n' +
        '  </metadata>\n' +
        '  <manifest>\n' +
        '    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>\n' +
        '    <item id="ch1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>\n' +
        '  </manifest>\n' +
        '  <spine><itemref idref="ch1"/></spine>\n' +
        '</package>\n',
    },
    {
      name: 'OEBPS/chapter1.xhtml',
      data:
        '<?xml version="1.0" encoding="UTF-8"?>\n' +
        '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter 1</title></head>\n' +
        `<body><h1>Chapter 1</h1><p>${paragraph}</p></body></html>\n`,
    },
    {
      name: 'OEBPS/nav.xhtml',
      data:
        '<?xml version="1.0" encoding="UTF-8"?>\n' +
        '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Contents</title></head>\n' +
        '<body><h1>Contents</h1><p><a href="chapter1.xhtml">Chapter 1</a></p></body></html>\n',
    },
  ]);
}

function makePdf(title, line) {
  const objects = [];
  const body = `BT /F1 12 Tf 72 720 Td (${line}) Tj ET`;
  objects.push('<< /Type /Catalog /Pages 2 0 R >>');
  objects.push('<< /Type /Pages /Kids [3 0 R] /Count 1 >>');
  objects.push(
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ' +
      '/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
  );
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  objects.push(`<< /Length ${Buffer.byteLength(body, 'latin1')} >>\nstream\n${body}\nendstream`);

  let pdf = '%PDF-1.4\n';
  const offsets = [0];
  objects.forEach((object, index) => {
    offsets.push(Buffer.byteLength(pdf, 'latin1'));
    pdf += `${index + 1} 0 obj\n${object}\nendobj\n`;
  });
  const xrefOffset = Buffer.byteLength(pdf, 'latin1');
  pdf += `xref\n0 ${objects.length + 1}\n`;
  pdf += '0000000000 65535 f \n';
  for (let i = 1; i <= objects.length; i += 1) {
    pdf += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  }
  pdf +=
    `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\n` +
    `startxref\n${xrefOffset}\n%%EOF\n`;
  return Buffer.from(pdf, 'latin1');
}

/** Deterministic pseudo-random bytes (xorshift32) for media payloads. */
function pseudoRandomBytes(size, seedText) {
  const buffer = Buffer.alloc(size);
  let state = 0x9e3779b9;
  for (let i = 0; i < seedText.length; i += 1) {
    state = (state ^ seedText.charCodeAt(i)) >>> 0;
    state = Math.imul(state, 0x85ebca6b) >>> 0;
  }
  for (let i = 0; i < size; i += 1) {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    buffer[i] = state & 0xff;
  }
  return buffer;
}

export const LIBRARY_FIXTURES = {
  a: {
    name: 'Library A (source)',
    collections: { fiction: 'Fiction', essays: 'Essays' },
    books: [
      {
        key: 'lantern',
        type: 'ebook',
        title: 'The Lantern Keepers',
        author: 'Ada Verne',
        file: { name: 'lantern.epub', type: 'application/epub+zip' },
        collection: 'fiction',
      },
      {
        key: 'field',
        type: 'ebook',
        title: 'Field Notes on Static',
        author: 'Ivo Marsh',
        file: { name: 'field.pdf', type: 'application/pdf' },
        collection: 'essays',
      },
      {
        key: 'tide',
        type: 'audiobook',
        title: 'Tidewater Sessions',
        author: 'Neve Callas',
        narrator: 'Neve Callas',
        file: { name: 'tide.mp3', type: 'audio/mpeg' },
      },
      { key: 'almanac', type: 'ebook', title: 'The Unsorted Almanac', author: 'P. Quill' },
    ],
    notes: [
      {
        book: 'lantern',
        content: 'Why the beacon matters.',
        selectedText: 'the lantern never goes dark while a keeper draws breath',
        cfiRange: 'epubcfi(/6/4!/4/2/1:0)',
      },
      { book: 'lantern', content: 'Check the tide tables in chapter two.' },
      { book: 'field', content: 'Static is the signal.' },
    ],
    progress: { book: 'lantern', location: 'epubcfi(/6/4!/4/2/1:12)', percentage: 42 },
    writings: [
      { type: 'Folder', name: 'Journals' },
      { type: 'Document', name: 'Migration diary', content: 'Day 1: packed the shelves.' },
    ],
    expected: {
      books: 4,
      notes: 3,
      highlights: 1,
      collections: 2,
      collectionMemberships: 2,
      progress: { book: 'lantern', percentage: 42 },
      writingName: 'Migration diary',
      writingContent: 'Day 1: packed the shelves.',
    },
    bigMediaBytes: 17 * 1024 * 1024 + 512 * 1024,
  },
  b: {
    name: 'Library B (destination)',
    collections: { archive: 'Archive' },
    books: [
      {
        key: 'harbour',
        type: 'ebook',
        title: 'Harbour Ledger',
        author: 'Mira Stone',
        file: { name: 'harbour.epub', type: 'application/epub+zip' },
        collection: 'archive',
      },
      { key: 'second', type: 'ebook', title: 'Second Almanac', author: 'T. Bell' },
    ],
    notes: [{ book: 'harbour', content: 'Belongs to the old destination.' }],
    progress: { book: 'second', location: 'epubcfi(/6/4!/4/2/1:3)', percentage: 10 },
    writings: [],
    expected: {
      books: 2,
      notes: 1,
      highlights: 0,
      collections: 1,
      collectionMemberships: 1,
      progress: { book: 'second', percentage: 10 },
    },
  },
};

function makeMedia(kind, def, fixture) {
  if (kind === 'lantern') {
    return makeEpub(
      'The Lantern Keepers',
      'The lantern never goes dark while a keeper draws breath.',
    );
  }
  if (kind === 'harbour') {
    return makeEpub('Harbour Ledger', 'Every ledger begins with a single line.');
  }
  if (kind === 'field') {
    return makePdf('Field Notes on Static', 'Static is the signal.');
  }
  if (kind === 'tide') {
    return pseudoRandomBytes(fixture.bigMediaBytes, 'tidewater-sessions');
  }
  throw new Error(`No synthetic media for book '${kind}'`);
}

async function requestJson(baseUrl, method, urlPath, body) {
  const res = await fetch(`${baseUrl}${urlPath}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${urlPath} -> ${res.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}

async function uploadFile(baseUrl, bookId, bytes, fileName, contentType) {
  const form = new FormData();
  form.append('file', new Blob([bytes], { type: contentType }), fileName);
  const res = await fetch(`${baseUrl}/api/books/${bookId}/file`, { method: 'POST', body: form });
  if (!res.ok) throw new Error(`POST /api/books/${bookId}/file -> ${res.status}: ${await res.text()}`);
}

async function seedLibrary(baseUrl, variant) {
  const fixture = LIBRARY_FIXTURES[variant];
  if (!fixture) return null;

  const collectionIds = {};
  for (const [key, name] of Object.entries(fixture.collections)) {
    const created = await requestJson(baseUrl, 'POST', '/api/collections/', { name });
    collectionIds[key] = created.id;
  }

  const bookIds = {};
  for (const def of fixture.books) {
    const created = await requestJson(baseUrl, 'POST', '/api/books/', {
      type: def.type,
      title: def.title,
      author: def.author ?? null,
      narrator: def.narrator ?? null,
    });
    const id = created.id;
    bookIds[def.key] = id;
    if (def.file) {
      await uploadFile(
        baseUrl,
        id,
        makeMedia(def.key, def, fixture),
        def.file.name,
        def.file.type,
      );
    }
    if (def.collection) {
      await requestJson(baseUrl, 'PUT', `/api/books/${id}/collections`, {
        collectionIds: [collectionIds[def.collection]],
      });
    }
  }

  const noteIds = [];
  for (const note of fixture.notes) {
    const created = await requestJson(
      baseUrl,
      'POST',
      `/api/books/${bookIds[note.book]}/notes`,
      {
        content: note.content,
        selectedText: note.selectedText ?? null,
        cfiRange: note.cfiRange ?? null,
      },
    );
    noteIds.push(created.id);
  }

  if (fixture.progress) {
    await requestJson(baseUrl, 'PUT', `/api/books/${bookIds[fixture.progress.book]}/progress`, {
      location: fixture.progress.location,
      percentage: fixture.progress.percentage,
    });
  }

  for (const writing of fixture.writings) {
    const created = await requestJson(baseUrl, 'POST', '/api/writings/', {
      name: writing.name,
      type: writing.type,
      parentId: null,
    });
    if (writing.content) {
      await requestJson(baseUrl, 'PUT', `/api/writings/${created.id}`, {
        name: writing.name,
        content: writing.content,
      });
    }
  }

  return {
    variant,
    bookIds,
    noteIds,
    collectionIds,
    expected: fixture.expected,
  };
}

// --- commands ------------------------------------------------------------------

async function launchInstance(name, seed = 'none') {
  const state = readState();
  if (state.instances[name]) throw new Error(`Instance '${name}' already exists in state.`);
  const instance = newInstance(name, seed);
  log(`starting '${name}' (seed=${seed}) in ${instance.tempDir}`);
  await startInstance(instance);
  const seeded = seed === 'none' ? null : await seedLibrary(instance.baseUrl, seed);
  const entry = { ...instance, seeded };
  if (!state.facts) state.facts = {};
  state.instances[name] = entry;
  state.chunkSizeBytes = CHUNK_SIZE_BYTES;
  state.builtAt = state.builtAt ?? new Date().toISOString();
  writeState(state);
  log(`'${name}' ready at ${instance.baseUrl} (pid ${instance.backendPid})`);
  return entry;
}

async function launchAll() {
  build();
  // Self-heal a crashed previous run: stop and drop anything still recorded.
  const stale = readState();
  if (Object.keys(stale.instances ?? {}).length > 0) {
    log('stale instance state found; cleaning it up first');
    await killAll();
  }
  writeState({ instances: {}, chunkSizeBytes: CHUNK_SIZE_BYTES, builtAt: new Date().toISOString() });
  const names = Object.keys(BASE_INSTANCES);
  const results = await Promise.allSettled(
    names.map((name) => launchInstance(name, BASE_INSTANCES[name])),
  );
  const failures = results
    .map((result, index) => ({ result, name: names[index] }))
    .filter(({ result }) => result.status === 'rejected');
  if (failures.length > 0) {
    for (const { name, result } of failures) {
      log(`FAILED ${name}: ${result.reason?.stack ?? result.reason}`);
    }
    throw new Error(`Failed to launch ${failures.length} instance(s).`);
  }
  const final = readState();
  final.libraries = Object.fromEntries(
    Object.entries(LIBRARY_FIXTURES).map(([key, value]) => [key, value.expected]),
  );
  writeState(final);
  log(`fixture ready with ${names.length} instances`);
}

async function restartInstance(name, { hard = false } = {}) {
  const state = readState();
  const instance = state.instances[name];
  if (!instance) throw new Error(`Unknown instance '${name}'.`);
  log(`restarting '${name}' (pid ${instance.backendPid}, hard=${hard}) over ${instance.tempDir}`);
  if (instance.backendPid) {
    try {
      // A hard restart simulates a crash: SIGKILL, no graceful shutdown wait.
      process.kill(instance.backendPid, hard ? 'SIGKILL' : 'SIGTERM');
    } catch (err) {
      log(`pid ${instance.backendPid} already gone: ${err.message}`);
    }
  }
  await new Promise((resolve) => setTimeout(resolve, hard ? 200 : 1_200));
  await waitForPortFree(instance.port);
  const child = spawnBackend(instance);
  instance.backendPid = child.pid;
  instance.startedAt = new Date().toISOString();
  await waitForHealthy(instance.baseUrl);
  state.instances[name] = instance;
  writeState(state);
  log(`'${name}' restarted (pid ${child.pid})`);
}

async function killInstance(name, { preserveRoot = false } = {}) {
  const state = readState();
  const instance = state.instances[name];
  if (!instance) {
    log(`'${name}' not in state; nothing to kill.`);
    return;
  }
  log(`stopping '${name}' (pid ${instance.backendPid})`);
  if (instance.backendPid) {
    try {
      process.kill(instance.backendPid, 'SIGTERM');
    } catch {
      // already gone
    }
    await new Promise((resolve) => setTimeout(resolve, 1_500));
    try {
      process.kill(instance.backendPid, 'SIGKILL');
    } catch {
      // already gone
    }
  }
  const logPath = path.join(instance.tempDir, 'backend.log');
  if (existsSync(logPath)) {
    try {
      mkdirSync(TEST_RESULTS, { recursive: true });
      copyFileSync(logPath, path.join(TEST_RESULTS, `backend-${name}.log`));
    } catch (err) {
      log(`could not preserve ${name} log: ${err.message}`);
    }
  }
  if (!preserveRoot && process.env.E2E_KEEP_FIXTURE !== '1') {
    rmSync(instance.tempDir, { recursive: true, force: true });
  }
  delete state.instances[name];
  writeState(state);
  log(`'${name}' stopped`);
}

async function killAll() {
  const state = readState();
  const names = Object.keys(state.instances ?? {});
  for (const name of names) await killInstance(name);
  if (existsSync(STATE_FILE)) rmSync(STATE_FILE, { force: true });
  log(`all ${names.length} instance(s) stopped`);
}

// --- CLI -----------------------------------------------------------------------

const [command = 'launch-all', ...args] = process.argv.slice(2);
const run = async () => {
  switch (command) {
    case 'launch-all':
      return launchAll();
    case 'launch-instance':
      return launchInstance(args[0], args[1] ?? 'none');
    case 'restart-instance':
      return restartInstance(args[0], { hard: args.includes('--hard') });
    case 'kill-instance':
      return killInstance(args[0]);
    case 'kill-all':
      return killAll();
    default:
      throw new Error(`Unknown command '${command}'.`);
  }
};

run().catch((err) => {
  console.error(`[lt-fixture] ${command} failed: ${err.stack ?? err.message}`);
  process.exit(1);
});
