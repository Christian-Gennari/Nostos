/**
 * Global teardown for the library-transfer real-backend browser suite: stop
 * every fixture instance (source and any on-demand destination left behind by
 * a crashed test) and remove the disposable roots.
 */
import { spawnSync } from 'node:child_process';
import path from 'node:path';

export default function globalTeardown(): void {
  const launcher = path.join(__dirname, 'library-transfer-fixture.mjs');
  const result = spawnSync(process.execPath, [launcher, 'kill-all'], {
    stdio: 'inherit',
    timeout: 180_000,
  });
  if (result.status !== 0) {
    console.error(`[lt-global-teardown] kill-all exited ${result.status} (non-fatal).`);
  }
}
