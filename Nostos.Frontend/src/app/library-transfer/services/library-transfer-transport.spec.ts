import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
} from './library-transfer-transport';
import { HttpLibraryTransferTransport } from './http-library-transfer-transport';

/**
 * Raw source text of every application module, keyed by path. The in-memory
 * mock transport is test-only: no non-spec, non-testing file may import it, or
 * the production bundle could ship fake migration state.
 */
const appSources = import.meta.glob('../../**/*.ts', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;

describe('library transfer transport DI', () => {
  it('provides the real SelfHosted HTTP adapter by default', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    const transport: LibraryTransferTransport = TestBed.inject(LIBRARY_TRANSFER_TRANSPORT);

    expect(transport).toBeInstanceOf(HttpLibraryTransferTransport);
  });

  it('never imports the in-memory mock from a non-test source file', () => {
    const paths = Object.keys(appSources);
    // Prove the raw-source scan is not vacuous before trusting the filter.
    expect(paths.length).toBeGreaterThan(100);
    expect(paths.some((path) => path.endsWith('testing/mock-library-transfer-transport.ts'))).toBe(
      true,
    );

    const offenders = Object.entries(appSources)
      .filter(([path]) => !path.includes('.spec.'))
      .filter(([path]) => !path.includes('/testing/'))
      .filter(([, source]) =>
        /mock-library-transfer-transport|MockLibraryTransferTransport/.test(source),
      )
      .map(([path]) => path);

    expect(offenders).toEqual([]);
  });
});
