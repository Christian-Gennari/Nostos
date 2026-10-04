import { TestBed } from '@angular/core/testing';

import {
  LIBRARY_TRANSFER_TRANSPORT,
  createLibraryTransferTransport,
} from './library-transfer-transport';

/**
 * Raw source text of every application module, keyed by path. The in-memory
 * mock transport is test-only: no non-spec, non-testing file may import it, or
 * the production bundle could ship fake migration state (slice B7 owns the
 * real adapter).
 */
const appSources = import.meta.glob('../../**/*.ts', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

describe('library transfer transport DI', () => {
  it('fails closed when no real transport is configured', () => {
    expect(() => createLibraryTransferTransport()).toThrowError(
      'library transfer transport is not configured',
    );
    expect(() => TestBed.inject(LIBRARY_TRANSFER_TRANSPORT)).toThrowError(
      'library transfer transport is not configured',
    );
  });

  it('never imports the in-memory mock from a non-test source file', () => {
    const paths = Object.keys(appSources);
    // Prove the raw-source scan is not vacuous before trusting the filter.
    expect(paths.length).toBeGreaterThan(100);
    expect(
      paths.some((path) => path.endsWith('mock-library-transfer-transport.service.ts')),
    ).toBe(true);

    const offenders = Object.entries(appSources)
      .filter(([path]) => !path.includes('.spec.'))
      .filter(([path]) => !path.includes('/testing/'))
      .filter(([path]) => !path.endsWith('mock-library-transfer-transport.service.ts'))
      .filter(([, source]) =>
        /mock-library-transfer-transport|MockLibraryTransferTransport/.test(source),
      )
      .map(([path]) => path);

    expect(offenders).toEqual([]);
  });
});
