import {
  TRANSFER_TAB_LEASE_KEY,
  TransferTabLease,
  TransferTabLeaseRecord,
} from './transfer-tab-lease.service';

describe('TransferTabLease', () => {
  const leases: TransferTabLease[] = [];

  function create(overrides: Partial<TransferTabLease> = {}): TransferTabLease {
    const lease = new TransferTabLease();
    Object.assign(lease, overrides);
    leases.push(lease);
    return lease;
  }

  function writeRecord(record: TransferTabLeaseRecord): void {
    localStorage.setItem(TRANSFER_TAB_LEASE_KEY, JSON.stringify(record));
  }

  function dispatchStorage(newValue: string | null): void {
    window.dispatchEvent(
      new StorageEvent('storage', {
        key: TRANSFER_TAB_LEASE_KEY,
        newValue,
        storageArea: localStorage,
      }),
    );
  }

  beforeEach(() => localStorage.removeItem(TRANSFER_TAB_LEASE_KEY));

  afterEach(() => {
    for (const lease of leases.splice(0)) lease.ngOnDestroy();
    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    vi.useRealTimers();
  });

  it('claims an uncontended lease and considers itself the owner', () => {
    const first = create();

    expect(first.otherTabActive()).toBe(false);
    expect(first.claim('library.nostos')).toBe(true);
    expect(first.otherTabActive()).toBe(false);

    const record = JSON.parse(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)!);
    expect(record.fileName).toBe('library.nostos');
  });

  it('refuses a second tab’s claim while the first is fresh and reports the other tab', () => {
    const first = create();
    expect(first.claim()).toBe(true);

    const second = create();
    expect(second.claim()).toBe(false);
    expect(second.otherTabActive()).toBe(true);

    // The first tab's record survived the refused claim.
    const record = JSON.parse(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)!);
    expect(record.tabId).not.toBe('other-tab');
  });

  it('reacts to a storage event from another tab and to its removal', () => {
    const lease = create();
    expect(lease.otherTabActive()).toBe(false);

    const other: TransferTabLeaseRecord = {
      tabId: 'other-tab',
      updatedAt: Date.now(),
      fileName: 'elsewhere.nostos',
    };
    writeRecord(other);
    dispatchStorage(JSON.stringify(other));

    expect(lease.otherTabActive()).toBe(true);
    expect(lease.otherTabFileName()).toBe('elsewhere.nostos');

    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    dispatchStorage(null);
    expect(lease.otherTabActive()).toBe(false);
  });

  it('treats a lease older than its TTL as abandoned', () => {
    const lease = create({ leaseTtlMs: 1_000, now: () => 10_000 });
    writeRecord({ tabId: 'crashed-tab', updatedAt: 8_000 });
    lease.refresh();

    expect(lease.otherTabActive()).toBe(false);
  });

  it('takes over an expired lease', () => {
    const lease = create({ leaseTtlMs: 1_000, now: () => 10_000 });
    writeRecord({ tabId: 'crashed-tab', updatedAt: 5_000 });

    expect(lease.claim()).toBe(true);
    const record = JSON.parse(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)!);
    expect(record.tabId).not.toBe('crashed-tab');
    expect(record.updatedAt).toBe(10_000);
  });

  it('never removes another tab’s lease on release', () => {
    const lease = create();
    writeRecord({ tabId: 'other-tab', updatedAt: Date.now() });

    lease.release();

    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).not.toBeNull();
  });

  it('keeps the lease fresh while the heartbeat runs', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-04T00:00:00Z'));

    const owner = create({ heartbeatMs: 100, leaseTtlMs: 250 });
    expect(owner.claim()).toBe(true);

    vi.advanceTimersByTime(400);

    const observer = create({ leaseTtlMs: 250 });
    expect(observer.otherTabActive()).toBe(true);
  });
});
