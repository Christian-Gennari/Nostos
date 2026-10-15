/**
 * Global setup for Task 13 Playwright: launch the isolated fixture
 * (fresh temp SQLite DB, freshly built Angular app served by the real
 * ASP.NET Core backend).
 */
import { spawnSync } from 'node:child_process';
import path from 'node:path';

export default function globalSetup(): void {
  const launcher = path.join(__dirname, 'launch-fixture.mjs');
  const result = spawnSync(process.execPath, [launcher, 'launch'], {
    stdio: 'inherit',
    timeout: 900_000,
  });
  if (result.status !== 0) {
    throw new Error(`Fixture launch failed (exit ${result.status}). See [fixture] output above.`);
  }
}
