import {
  MOCK_TRANSFER_TRANSPORT_OPT_IN,
  createLibraryTransferTransport,
  mockTransferTransportAllowed,
} from './library-transfer-transport';
import { MockLibraryTransferTransport } from './mock-library-transfer-transport.service';

describe('library transfer transport guard', () => {
  afterEach(() => {
    delete (globalThis as Record<string, unknown>)[MOCK_TRANSFER_TRANSPORT_OPT_IN];
  });

  it('refuses the in-memory mock in a production build without an explicit opt-in', () => {
    expect(mockTransferTransportAllowed(false, false)).toBe(false);
    expect(() => createLibraryTransferTransport(false, false)).toThrowError(
      /production build/,
    );
  });

  it('allows the mock in development and test builds', () => {
    expect(mockTransferTransportAllowed(true, false)).toBe(true);
    expect(createLibraryTransferTransport(true, false)).toBeInstanceOf(
      MockLibraryTransferTransport,
    );
  });

  it('allows an explicit production opt-in for deliberate local runs', () => {
    expect(mockTransferTransportAllowed(false, true)).toBe(true);
    expect(createLibraryTransferTransport(false, true)).toBeInstanceOf(
      MockLibraryTransferTransport,
    );
  });
});
