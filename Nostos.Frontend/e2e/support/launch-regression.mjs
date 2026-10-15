#!/usr/bin/env node
/**
 * Task 13 launcher lifecycle regression — deterministic, fixture-only.
 *
 * Guards the launcher repair. The launch CLI previously NEVER exited: the
 * backend child's piped stdio kept the launcher's event loop alive, which
 * froze Playwright global setup before a single test ran.
 *
 * This script proves, without touching any production path (no production
 * DB/config/ports — everything happens inside the temp fixture under
 * os.tmpdir, via the launcher's own commands):
 *
 *   1. `launch` CLI returns within a bounded wall-clock time and exits 0
 *      (E2E_SKIP_BUILDS=1 reuses existing artifacts so the bound measures
 *      the lifecycle, not compile time);
 *   2. the detached backend survives the CLI exit, stays healthy, and its
 *      log file receives output (fd redirection works);
 *   3. `restart` still works: new pid, healthy, same DB/port;
 *   4. `kill` cleans up: CLI exits, process gone, port free, temp dir and
 *      state file removed.
 *
 * Usage: node e2e/support/launch-regression.mjs   (or npm run e2e:launcher-check)
 */
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const E2E_DIR = path.resolve(HERE, '..');
const FRONTEND_DIR = path.resolve(E2E_DIR, '..');
const REPO_ROOT = path.resolve(FRONTEND_DIR, '..');
const BACKEND_DIR = path.join(REPO_ROOT, 'Nostos.Backend');
const LAUNCHER = path.join(HERE, 'launch-fixture.mjs');
const STATE_FILE = path.join(HERE, 'fixture-state.json');

const DLL = path.join(BACKEND_DIR, 'bin', 'Debug', 'net10.0', 'Nostos.Backend.dll');
const DIST_INDEX = path.join(FRONTEND_DIR, 'dist', 'Nostos.Frontend', 'browser', 'index.html');

// The old hung behavior exceeded ANY bound; these are generous but bounded.
const LAUNCH_BOUND_MS = 180_000;
const RESTART_BOUND_MS = 180_000;
const KILL_BOUND_MS = 60_000;

let failures = 0;
function check(ok, name, detail = '') {
  console.log(`  ${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` (${detail})` : ''}`);
  if (!ok) failures += 1;
}

/** Runs the launcher CLI as a subprocess, capturing output, bounded by boundMs. */
function runCli(args, boundMs) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [LAUNCHER, ...args], {
      env: { ...process.env, E2E_SKIP_BUILDS: '1' },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let out = '';
    let err = '';
    child.stdout.on('data', (d) => {
      out += d;
    });
    child.stderr.on('data', (d) => {
      err += d;
    });
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      resolve({ timedOut: true, status: null, signal: 'SIGKILL', out, err, elapsedMs: Date.now() - started });
    }, boundMs);
    child.on('error', (e) => {
      clearTimeout(timer);
      resolve({ timedOut: false, status: null, signal: null, out, err, elapsedMs: Date.now() - started, error: e.message });
    });
    child.on('close', (code, signal) => {
      clearTimeout(timer);
      resolve({ timedOut: false, status: code, signal, out, err, elapsedMs: Date.now() - started });
    });
  });
}

/** Builds whatever artifact is missing so the bounded lifecycle run is self-contained. */
function ensureArtifacts() {
  const steps = [
    { label: 'dotnet build backend', cmd: 'dotnet', args: ['build', path.join(BACKEND_DIR, 'Nostos.Backend.csproj'), '-c', 'Debug', '--nologo', '-v', 'q'], artifact: DLL },
    { label: 'npm run build (frontend)', cmd: 'npm', args: ['run', 'build'], artifact: DIST_INDEX },
  ];
  for (const step of steps) {
    if (existsSync(step.artifact)) continue;
    console.log(`  building missing artifact (${path.basename(step.artifact)})...`);
    const r = spawnSync(step.cmd, step.args, { cwd: FRONTEND_DIR, stdio: 'inherit', timeout: 600_000 });
    if (r.status !== 0) {
      console.error(`build step failed: ${step.label} (exit ${r.status})`);
      process.exit(2);
    }
  }
  console.log('  artifacts ready (backend DLL, Angular dist)');
}

function alive(pid) {
  if (!pid) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function waitHealthy(baseUrl, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  let lastErr;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`${baseUrl}/api/books?pageSize=1`);
      if (res.ok) return true;
      lastErr = new Error(`HTTP ${res.status}`);
    } catch (err) {
      lastErr = err;
    }
    await new Promise((r) => setTimeout(r, 400));
  }
  console.error(`    health probe failed: ${lastErr?.message ?? 'timeout'}`);
  return false;
}

function portFree(port, timeoutMs = 30_000) {
  return new Promise((resolve) => {
    const deadline = Date.now() + timeoutMs;
    const tryListen = () => {
      if (Date.now() > deadline) return resolve(false);
      const srv = net.createServer();
      srv.once('error', () => setTimeout(tryListen, 300));
      srv.listen(port, '127.0.0.1', () => srv.close(() => resolve(true)));
    };
    tryListen();
  });
}

async function main() {
  console.log('=== launcher lifecycle regression ===');
  ensureArtifacts();

  console.log('--- 1. launch CLI returns within bound, exits 0 ---');
  const launchRes = await runCli(['launch'], LAUNCH_BOUND_MS);
  check(
    !launchRes.timedOut,
    'launch CLI exited before bound (previously hung forever)',
    launchRes.timedOut ? `hung >${LAUNCH_BOUND_MS}ms, killed` : `${launchRes.elapsedMs}ms`
  );
  check(
    launchRes.status === 0,
    'launch CLI exit code 0',
    launchRes.status === 0 ? '' : `status=${launchRes.status} signal=${launchRes.signal}${launchRes.error ? ` error=${launchRes.error}` : ''}`
  );

  let state = null;
  if (existsSync(STATE_FILE)) {
    state = JSON.parse(readFileSync(STATE_FILE, 'utf8'));
  } else {
    check(false, 'fixture-state.json written by launch');
  }

  if (state) {
    console.log('--- 2. detached backend survives CLI exit, healthy, log captured ---');
    check(alive(state.backendPid), 'backend process alive after CLI exit', `pid ${state.backendPid}`);
    check(await waitHealthy(state.baseUrl), 'backend health endpoint responds', state.baseUrl);
    const logPath = path.join(state.tempDir, 'backend.log');
    const logOk = existsSync(logPath) && statSync(logPath).size > 0;
    check(logOk, 'backend.log received output (fd redirection works)', logOk ? logPath : 'missing or empty');

    console.log('--- 3. restart still works (new pid, healthy, same DB) ---');
    const oldPid = state.backendPid;
    const restartRes = await runCli(['restart'], RESTART_BOUND_MS);
    check(
      !restartRes.timedOut && restartRes.status === 0,
      'restart CLI exits 0 within bound',
      `${restartRes.elapsedMs}ms status=${restartRes.status}${restartRes.timedOut ? ' (killed)' : ''}`
    );
    state = JSON.parse(readFileSync(STATE_FILE, 'utf8'));
    check(state.backendPid !== oldPid, 'restart produced a new backend pid', `old=${oldPid} new=${state.backendPid}`);
    check(alive(state.backendPid), 'restarted backend process alive');
    check(await waitHealthy(state.baseUrl), 'restarted backend healthy');

    console.log('--- 4. kill cleans up (process, port, temp dir, state) ---');
    const killRes = await runCli(['kill'], KILL_BOUND_MS);
    check(
      !killRes.timedOut && killRes.status === 0,
      'kill CLI exits 0 within bound',
      `${killRes.elapsedMs}ms status=${killRes.status}${killRes.timedOut ? ' (killed)' : ''}`
    );
    check(!existsSync(STATE_FILE), 'fixture-state.json removed');
    check(!existsSync(state.tempDir), 'temp dir removed', state.tempDir);
    check(await portFree(state.port), 'port freed', `port ${state.port}`);
    check(!alive(state.backendPid), 'backend process gone');
  }

  if (failures > 0 && existsSync(STATE_FILE)) {
    // Best-effort cleanup so a failed run never leaves a stray backend.
    spawnSync(process.execPath, [LAUNCHER, 'kill'], {
      env: { ...process.env, E2E_SKIP_BUILDS: '1' },
      stdio: 'ignore',
      timeout: 60_000,
    });
  }

  console.log(
    failures === 0
      ? 'LAUNCHER REGRESSION: ALL CHECKS PASSED'
      : `LAUNCHER REGRESSION: ${failures} CHECK(S) FAILED`
  );
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error(`launcher regression crashed: ${err.stack}`);
  process.exit(1);
});
