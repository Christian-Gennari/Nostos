#!/usr/bin/env node
/**
 * Task 13 Playwright fixture launcher.
 *
 * Owns a fully isolated Nostos instance for cross-surface E2E:
 *   - a fresh temp directory (temp DB via --contentRoot, temp wwwroot with a
 *     freshly built Angular app, backend logs);
 *   - the real ASP.NET Core backend (Debug build of Nostos.Backend.dll) on a
 *     random free 127.0.0.1 port;
 *   - deterministic lifecycle: `launch` (build + start + wait healthy),
 *     `restart` (same DB/wwwroot, new process — persistence check),
 *     `kill` (terminate + clean up temp dir, keep backend.log evidence).
 *
 * The host work (builds, free ports, health and port probes, the detached
 * spawn, stopping and log keeping) is shared with the library-transfer
 * launcher in `host-launcher.mjs`.
 *
 * State is written to e2e/support/fixture-state.json so specs, global
 * setup/teardown, and the restart path all agree on the same instance.
 */
import { cpSync, mkdtempSync, existsSync, readFileSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  DIST_BROWSER,
  E2E_DIR,
  REPO_ROOT,
  createLogger,
  ensureBuilds,
  freePort,
  preserveBackendLog,
  spawnBackend,
  stopProcess,
  waitForHealthy,
  waitForPortFree,
} from './host-launcher.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const STATE_FILE = path.join(HERE, 'fixture-state.json');

const HEALTH_TIMEOUT_MS = 240_000;

const log = createLogger('fixture');

function readState() {
  if (!existsSync(STATE_FILE)) throw new Error(`Fixture state missing (${STATE_FILE}); run launch first.`);
  return JSON.parse(readFileSync(STATE_FILE, 'utf8'));
}

function writeState(state) {
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
}

export async function launch() {
  ensureBuilds(log);

  const tempDir = mkdtempSync(path.join(tmpdir(), 'nostos-e2e-'));
  const wwwroot = path.join(tempDir, 'wwwroot');
  cpSync(DIST_BROWSER, wwwroot, { recursive: true });
  // The backend creates and bootstraps its own empty DB at
  // <contentRoot>/nostos.db on startup (DatabaseBootstrapService), so the
  // fixture needs no separate bootstrap step here.

  const port = await freePort();
  const state = {
    baseUrl: `http://127.0.0.1:${port}`,
    port,
    tempDir,
    repoRoot: REPO_ROOT,
    backendPid: null,
    startedAt: new Date().toISOString(),
  };

  log(`starting backend on ${state.baseUrl} (content root ${tempDir})`);
  const child = spawnBackend({ tempDir, port, log });
  state.backendPid = child.pid;
  writeState(state);

  try {
    await waitForHealthy(state.baseUrl, { timeoutMs: HEALTH_TIMEOUT_MS });
  } catch (err) {
    child.kill('SIGKILL');
    log(`backend log: ${path.join(tempDir, 'backend.log')}`);
    throw err;
  }

  log(`fixture ready: ${state.baseUrl} (pid ${child.pid})`);
  return state;
}

export async function restart() {
  const state = readState();
  log(`restarting backend on ${state.baseUrl} (pid ${state.backendPid}, same DB ${state.tempDir}/nostos.db)`);

  await stopProcess(state.backendPid, { graceMs: 1_500, log });
  await waitForPortFree(state.port);

  const child = spawnBackend({ tempDir: state.tempDir, port: state.port, log });
  state.backendPid = child.pid;
  state.startedAt = new Date().toISOString();
  writeState(state);

  try {
    await waitForHealthy(state.baseUrl, { timeoutMs: HEALTH_TIMEOUT_MS });
  } catch (err) {
    child.kill('SIGKILL');
    log(`backend log: ${path.join(state.tempDir, 'backend.log')}`);
    throw err;
  }
  log(`backend restarted (pid ${child.pid})`);
  return state;
}

export async function killFixture() {
  const state = readState();
  log(`tearing down fixture (pid ${state.backendPid}, temp ${state.tempDir})`);
  await stopProcess(state.backendPid, { graceMs: 2_000, log });

  // Keep backend.log as failure evidence next to the Playwright artifacts.
  preserveBackendLog(state.tempDir, path.join(E2E_DIR, 'test-results'), `backend-${state.port}.log`, log);

  if (process.env.E2E_KEEP_FIXTURE === '1') {
    log(`E2E_KEEP_FIXTURE=1: keeping temp dir ${state.tempDir}`);
  } else {
    rmSync(state.tempDir, { recursive: true, force: true });
  }
  unlinkSync(STATE_FILE);
}

// --- CLI entry ---
const [command = 'launch'] = process.argv.slice(2);
if (command === 'launch') {
  launch().catch((err) => {
    console.error(`[fixture] launch failed: ${err.message}`);
    process.exit(1);
  });
} else if (command === 'restart') {
  restart().catch((err) => {
    console.error(`[fixture] restart failed: ${err.message}`);
    process.exit(1);
  });
} else if (command === 'kill') {
  killFixture().catch((err) => {
    console.error(`[fixture] kill failed: ${err.message}`);
    process.exit(1);
  });
} else {
  console.error(`[fixture] unknown command '${command}' (expected launch|restart|kill)`);
  process.exit(2);
}
