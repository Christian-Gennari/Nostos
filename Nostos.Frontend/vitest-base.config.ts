import { readFileSync } from 'node:fs';
import { availableParallelism } from 'node:os';
import path from 'node:path';

import { defineConfig } from 'vitest/config';

// The Angular unit-test builder defaults to `test.isolate: false` ("to align
// with the Karma/Jasmine experience"), so every spec file in a worker shares one
// module registry. Two spec files that mock the same module then fight over it —
// `sigma` is mocked in both `topic-map.component.spec.ts` and
// `second-brain.component.spec.ts` — and which factory wins depends on how files
// are grouped per worker, which is timing-dependent. The suite therefore passed
// on an idle 12-core machine and failed on a two-core runner (CI): 13 reader
// specs and 22 topic-map specs, all reporting an uninitialised mock.
//
// The `?raw` plugin below lets the test-only contract-parity spec read the
// merged backend C# sources one directory above the frontend project. Vite's
// module runner denies file IDs outside its serving root, so the plugin
// resolves them to a virtual module and loads the text itself.
//
// This file is only read when the test target sets `runnerConfig: true`.
const VIRTUAL_PREFIX = '\0nostos-backend-source:';

function backendSourcePlugin() {
  return {
    name: 'nostos-backend-source-raw',
    enforce: 'pre' as const,
    resolveId(source: string, importer?: string) {
      if (!source.endsWith('.cs?raw')) return null;
      const clean = source.slice(0, -'.cs?raw'.length) + '.cs';
      const absolute = path.isAbsolute(clean)
        ? clean
        : path.resolve(importer ? path.dirname(importer) : process.cwd(), clean);
      return VIRTUAL_PREFIX + absolute;
    },
    load(id: string) {
      if (!id.startsWith(VIRTUAL_PREFIX)) return null;
      const file = id.slice(VIRTUAL_PREFIX.length);
      return `export default ${JSON.stringify(readFileSync(file, 'utf8'))};`;
    },
  };
}

// One worker per core is vitest's default. With `isolate: true` every worker
// loads Angular afresh, about 280 MB each, and the machine these suites run on
// also serves production: twelve workers beside a backend suite filled its
// swap and pushed the load average past 400. Four workers finish the suite in
// about a minute, which was faster than twelve on that machine.
// VITEST_MAX_WORKERS overrides the cap in either direction.
const requestedWorkers = Number(process.env['VITEST_MAX_WORKERS']);
const maxWorkers =
  Number.isInteger(requestedWorkers) && requestedWorkers > 0
    ? requestedWorkers
    : Math.min(4, availableParallelism());

export default defineConfig({
  plugins: [backendSourcePlugin()],
  test: {
    isolate: true,
    maxWorkers,
  },
});
