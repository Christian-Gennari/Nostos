/**
 * Global setup for the library-transfer (#680 slice B10) real-backend browser
 * suite: build once, then launch the immutable export-source instance.
 * Scenario destinations are launched per test by the harness so browser
 * projects and repeat runs never share consumed state.
 */
import { spawnSync } from 'node:child_process';
import path from 'node:path';

export default function globalSetup(): void {
  const launcher = path.join(__dirname, 'library-transfer-fixture.mjs');
  const result = spawnSync(process.execPath, [launcher, 'launch-all'], {
    stdio: 'inherit',
    timeout: 900_000,
  });
  if (result.status !== 0) {
    throw new Error(`Library-transfer fixture launch failed (exit ${result.status}).`);
  }
}
