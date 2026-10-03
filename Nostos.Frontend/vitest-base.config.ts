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
// This file is only read when the test target sets `runnerConfig: true`.
export default defineConfig({
  test: {
    isolate: true,
  },
});
