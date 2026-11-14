import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { CarrelService, formatPairingCode, isCompletePairingCode } from './carrel.service';

describe('CarrelService', () => {
  let service: CarrelService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(CarrelService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('looks a pairing code up without changing the code’s symbols', () => {
    service.findPending('ABC-234').subscribe();

    const request = http.expectOne('/api/carrel/pairing/ABC-234');
    expect(request.request.method).toBe('GET');
    request.flush({ code: 'ABC-234' });
  });

  it('escapes a code that needs it', () => {
    service.findPending('A/B').subscribe();

    const request = http.expectOne((candidate) => candidate.url.startsWith('/api/carrel/pairing/'));
    expect(request.request.url).toBe('/api/carrel/pairing/A%2FB');
    request.flush({});
  });

  it('approves and denies a device’s code', () => {
    service.approve('ABC-234').subscribe();
    const approve = http.expectOne('/api/carrel/pairing/ABC-234/approve');
    expect(approve.request.method).toBe('POST');
    expect(approve.request.body).toEqual({});
    approve.flush({});

    service.deny('ABC-234').subscribe();
    const deny = http.expectOne('/api/carrel/pairing/ABC-234/deny');
    expect(deny.request.method).toBe('POST');
    deny.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('lists and revokes the paired devices', () => {
    service.devices().subscribe();
    const devices = http.expectOne('/api/carrel/devices');
    expect(devices.request.method).toBe('GET');
    devices.flush({ devices: [] });

    service.revoke('device 1').subscribe();
    const revoke = http.expectOne('/api/carrel/devices/device%201');
    expect(revoke.request.method).toBe('DELETE');
    revoke.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('passes an unknown code through as a 404', () => {
    let status: number | undefined;
    service.findPending('ZZZ-999').subscribe({
      error: (error: { status: number }) => (status = error.status),
    });

    http.expectOne('/api/carrel/pairing/ZZZ-999').flush(
      { message: 'No device is waiting' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(status).toBe(404);
  });
});

describe('formatPairingCode', () => {
  it.each([
    ['abc234', 'ABC-234'],
    ['ABC-234', 'ABC-234'],
    ['  abc 234  ', 'ABC-234'],
    ['a-b-c-2-3-4', 'ABC-234'],
  ])('folds %s into a complete code', (typed, expected) => {
    expect(formatPairingCode(typed)).toBe(expected);
    expect(isCompletePairingCode(typed)).toBe(true);
  });

  it('drops symbols a person cannot read aloud', () => {
    // 0, 1, I, L, O and U are not in the alphabet.
    expect(formatPairingCode('0O1ILU')).toBe('');
  });

  it('allows at most six symbols, a dash only after the third', () => {
    expect(formatPairingCode('AB')).toBe('AB');
    expect(formatPairingCode('ABC')).toBe('ABC');
    expect(formatPairingCode('ABC2')).toBe('ABC-2');
    expect(formatPairingCode('ABC234ZZZ')).toBe('ABC-234');
    expect(isCompletePairingCode('ABC-23')).toBe(false);
    expect(isCompletePairingCode('')).toBe(false);
    expect(isCompletePairingCode(null)).toBe(false);
    expect(isCompletePairingCode(undefined)).toBe(false);
  });
});
