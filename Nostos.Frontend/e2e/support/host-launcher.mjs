/**
 * Host/port/build plumbing shared by the E2E fixture launchers.
 *
 * `launch-fixture.mjs` (the Playwright smoke suite's single instance) and
 * `library-transfer-fixture.mjs` (the multi-instance transfer acceptance
 * fixture) own different state files and different CLI commands, but both need
 * the same host work: find a free loopback port, build the backend and the
 * Angular app, spawn the real ASP.NET Core backend detached over a disposable
 * content root, wait until it answers, stop it again and keep its log. That
 * lives here once, so a fix to the lifecycle cannot land in one launcher and
 * miss the other.
 *
 * Nothing here reads or writes a state file: the state contract belongs to each
 * launcher.
 */
import { spawn, spawnSync } from 'node:child_process';
import { closeSync, copyFileSync, existsSync, mkdirSync, openSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

export const E2E_DIR = path.resolve(HERE, '..');
export const FRONTEND_DIR = path.resolve(E2E_DIR, '..');
export const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
export const BACKEND_DIR = path.join(REPO_ROOT, 'Nostos.Backend');
export const BACKEND_DLL = path.join(BACKEND_DIR, 'bin', 'Debug', 'net10.0', 'Nostos.Backend.dll');
export const DIST_BROWSER = path.join(FRONTEND_DIR, 'dist', 'Nostos.Frontend', 'browser');

/** A logger that tags every line, so two fixtures in one terminal stay apart. */
export function createLogger(tag) {
  return (...args) => console.log(`[${tag}]`, ...args);
}

/** A port the OS just handed out on loopback, so nothing else can be using it. */
export function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

/** Polls the books endpoint until the backend answers, or throws. */
export async function waitForHealthy(
  baseUrl,
  { timeoutMs = 240_000, intervalMs = 500 } = {},
) {
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
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  throw new Error(`Backend at ${baseUrl} not healthy within ${timeoutMs}ms (${lastErr})`);
}

/** Waits until `port` can be bound again, i.e. the previous listener is gone. */
export async function waitForPortFree(port, { timeoutMs = 30_000, intervalMs = 400 } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const free = await new Promise((resolve) => {
      const server = net.createServer();
      server.once('error', () => resolve(false));
      server.listen(port, '127.0.0.1', () => server.close(() => resolve(true)));
    });
    if (free) return;
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  throw new Error(`Port ${port} did not free within ${timeoutMs}ms`);
}

/**
 * Builds the artifacts a fixture runs from, or verifies they are already there
 * when `E2E_SKIP_BUILDS=1` (which keeps a lifecycle measurement about the
 * lifecycle, not about compile time).
 */
export function ensureBuilds(log) {
  if (process.env.E2E_SKIP_BUILDS === '1') {
    for (const [label, artifact] of [
      ['backend DLL', BACKEND_DLL],
      ['Angular dist index', path.join(DIST_BROWSER, 'index.html')],
    ]) {
      if (!existsSync(artifact)) {
        throw new Error(`E2E_SKIP_BUILDS=1 but ${label} missing at ${artifact}`);
      }
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
  if (!existsSync(BACKEND_DLL)) throw new Error(`Backend DLL not found: ${BACKEND_DLL}`);

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

/**
 * Spawns the real backend as a DETACHED process over `tempDir`, with its
 * stdout/stderr redirected straight into `<tempDir>/backend.log` through
 * duplicated file descriptors (no pipes). Nothing keeps the launcher CLI's
 * event loop alive, so `launch`/`restart` return as soon as the health check
 * passes while the backend keeps running; pid-based restart/kill still work.
 *
 * `env` carries the fixture-specific settings (chunking, log level, ...); the
 * common ones are set here.
 */
export function spawnBackend({ tempDir, port, env = {}, log = () => {} }) {
  const logFd = openSync(path.join(tempDir, 'backend.log'), 'a');
  let child;
  try {
    child = spawn('dotnet', [BACKEND_DLL, '--contentRoot', tempDir, '--urls', `http://127.0.0.1:${port}`], {
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Production',
        DOTNET_NOLOGO: '1',
        ...env,
      },
      stdio: ['ignore', logFd, logFd],
      detached: true,
    });
  } finally {
    // The child holds its own duplicated descriptors; the parent copy is
    // closed so no handle on this side stays open.
    closeSync(logFd);
  }
  // The backend must outlive this CLI: unref the child handle so neither it
  // nor any pipe keeps the launcher process from exiting after it resolves.
  child.unref();
  child.on('exit', (code, signal) => log(`backend process exited code=${code} signal=${signal}`));
  child.on('error', (err) => log(`backend spawn error: ${err.message}`));
  return child;
}

/** SIGTERM, a grace period, then SIGKILL. A pid that is already gone is fine. */
export async function stopProcess(pid, { graceMs = 1_500, log = () => {} } = {}) {
  if (!pid) return;
  try {
    process.kill(pid, 'SIGTERM');
  } catch (err) {
    log(`pid ${pid} already gone (${err.message})`);
    return;
  }
  await new Promise((resolve) => setTimeout(resolve, graceMs));
  try {
    process.kill(pid, 'SIGKILL');
  } catch {
    // already exited
  }
}

/** Copies a disposable root's backend log beside the Playwright artifacts. */
export function preserveBackendLog(tempDir, destinationDir, fileName, log = () => {}) {
  const logPath = path.join(tempDir, 'backend.log');
  if (!existsSync(logPath)) return;
  try {
    mkdirSync(destinationDir, { recursive: true });
    const destination = path.join(destinationDir, fileName);
    copyFileSync(logPath, destination);
    log(`backend log preserved at ${destination}`);
  } catch (err) {
    log(`could not preserve backend log: ${err.message}`);
  }
}
