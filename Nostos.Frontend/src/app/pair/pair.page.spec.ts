import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import type { CarrelPairingApproved, CarrelPendingPairing } from '../core/dtos/carrel.dtos';
import { CarrelService, formatPairingCode, isCompletePairingCode } from '../core/services/carrel.service';
import { ToastService } from '../core/services/toast.service';
import { CODE_ENDED, NO_WAITING_DEVICE, PairPage } from './pair.page';

const device: CarrelPendingPairing = {
  code: 'K7Q-4MZ',
  deviceName: 'X250',
  expiresAt: '2026-11-12T09:30:00Z',
};

const approver = {
  findPending: vi.fn(() => of(device)),
  approve: vi.fn(() => of({ deviceId: 'd1', deviceName: 'X250' } as CarrelPairingApproved)),
  deny: vi.fn(() => of(undefined as void)),
  devices: vi.fn(() => of({ devices: [] })),
  revoke: vi.fn(() => of(undefined as void)),
};
const toast = { success: vi.fn(), error: vi.fn() };

async function configurePage(query: Record<string, string>): Promise<ComponentFixture<PairPage>> {
  await TestBed.configureTestingModule({
    imports: [PairPage],
    providers: [
      { provide: CarrelService, useValue: approver },
      { provide: ToastService, useValue: toast },
      provideRouter([]),
      // After provideRouter: the router provides an ActivatedRoute of its own,
      // and the later provider is the one a component injects.
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(query) } },
      },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(PairPage);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('PairPage', () => {
  let fixture: ComponentFixture<PairPage>;
  let component: PairPage;

  const el = (testId: string) =>
    fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null;
  const text = (testId: string) => el(testId)?.textContent?.replace(/\s+/g, ' ').trim();
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const typeCode = (value: string) => {
    const input = el('pair-code') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    fixture = await configurePage({});
    component = fixture.componentInstance;
  });

  afterEach(() => fixture.destroy());

  it('starts with an empty code and cannot look one up yet', () => {
    expect(el('pair-code')).not.toBeNull();
    expect((el('pair-find') as HTMLButtonElement).disabled).toBe(true);
    expect(el('pair-device')).toBeNull();
  });

  it('formats the code as it is typed and finds the device at six symbols', () => {
    typeCode('k7');
    expect((el('pair-code') as HTMLInputElement).value).toBe('K7');
    expect((el('pair-find') as HTMLButtonElement).disabled).toBe(true);

    typeCode('k7q4mz');
    expect(component.code()).toBe('K7Q-4MZ');
    expect(approver.findPending).toHaveBeenCalledWith('K7Q-4MZ');
    expect(text('pair-device-name')).toBe('X250');
    expect(text('pair-device')).toContain('This code expires at');
  });

  it('drops symbols the pairing alphabet does not have', () => {
    typeCode('0O1IlLuUk7q');
    expect((el('pair-code') as HTMLInputElement).value).toBe('K7Q');
    expect(component.code()).toBe('K7Q');
  });

  it('says when no device is waiting with that code', async () => {
    approver.findPending.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 404 })),
    );
    typeCode('K7Q-4MZ');
    await settle();

    expect(text('pair-error')).toBe(NO_WAITING_DEVICE);
    expect(el('pair-device')).toBeNull();
  });

  it('says when the server could not be reached, and finds it again on retry', async () => {
    approver.findPending.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    typeCode('K7Q-4MZ');
    await settle();
    expect(text('pair-error')).toContain('Could not reach this server');

    component.find();
    await settle();
    expect(el('pair-error')).toBeNull();
    expect(text('pair-device-name')).toBe('X250');
  });

  it('approves the device it found and says the device can sync', async () => {
    typeCode('K7Q-4MZ');
    el('pair-approve')!.click();
    await settle();

    expect(approver.approve).toHaveBeenCalledWith('K7Q-4MZ');
    expect(text('pair-approved')).toContain('X250');
    expect(text('pair-approved')).toContain('can sync with Nostos');
    expect(el('pair-device')).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('X250 can sync with Nostos');
  });

  it('denies the device and offers the code field again', async () => {
    typeCode('K7Q-4MZ');
    el('pair-deny')!.click();
    await settle();

    expect(approver.deny).toHaveBeenCalledWith('K7Q-4MZ');
    expect(text('pair-denied')).toContain('not paired');
    expect(el('pair-device')).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('X250 was not paired');

    component.startOver();
    await settle();
    expect((el('pair-code') as HTMLInputElement).value).toBe('');
  });

  it('treats a device that ended as expired, not as a failure', async () => {
    typeCode('K7Q-4MZ');
    approver.approve.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 404 })),
    );
    el('pair-approve')!.click();
    await settle();

    expect(text('pair-error')).toBe(CODE_ENDED);
    expect(el('pair-device')).toBeNull();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('keeps the found device when denying fails, and says so', async () => {
    typeCode('K7Q-4MZ');
    approver.deny.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    el('pair-deny')!.click();
    await settle();

    expect(el('pair-device')).not.toBeNull();
    expect(text('pair-error')).toContain('Could not deny');
    expect(component.busy()).toBeNull();
  });
});

describe('PairPage from a QR link', () => {
  let fixture: ComponentFixture<PairPage>;

  beforeEach(async () => {
    vi.clearAllMocks();
    fixture = await configurePage({ code: 'k7q4mz' });
  });

  afterEach(() => fixture.destroy());

  it('prefills the code and looks the device up straight away', () => {
    expect(approver.findPending).toHaveBeenCalledWith('K7Q-4MZ');
    const name = fixture.nativeElement.querySelector('[data-testid="pair-device-name"]');
    expect(name?.textContent).toContain('X250');
  });
});

describe('formatPairingCode', () => {
  it('uppercases, keeps only the alphabet symbols and adds the dash', () => {
    expect(formatPairingCode('k7q')).toBe('K7Q');
    expect(formatPairingCode('k7q4')).toBe('K7Q-4');
    expect(formatPairingCode('k7q 4mz')).toBe('K7Q-4MZ');
    expect(formatPairingCode('K7Q-4MZ-EXTRA')).toBe('K7Q-4MZ');
    expect(formatPairingCode('0O1IlLuU')).toBe('');
    expect(formatPairingCode(null)).toBe('');
  });

  it('knows when six symbols are there', () => {
    expect(isCompletePairingCode('k7q4mz')).toBe(true);
    expect(isCompletePairingCode('k7q4m')).toBe(false);
  });
});
