import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import type { CarrelDevice } from '../core/dtos/carrel.dtos';
import { CarrelService } from '../core/services/carrel.service';
import { ToastService } from '../core/services/toast.service';
import { CarrelDevicesSettingsComponent } from './carrel-devices-settings.component';

describe('CarrelDevicesSettingsComponent', () => {
  let fixture: ComponentFixture<CarrelDevicesSettingsComponent>;
  let component: CarrelDevicesSettingsComponent;
  let devices: CarrelDevice[];

  const api = {
    devices: vi.fn(() => of({ devices })),
    revoke: vi.fn(() => of(undefined as void)),
    findPending: vi.fn(),
    approve: vi.fn(),
    deny: vi.fn(),
  };
  const toast = { success: vi.fn(), error: vi.fn() };

  const el = (testId: string) =>
    fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null;
  const all = (testId: string) =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`)) as HTMLElement[];
  const text = (testId: string) => el(testId)?.textContent?.replace(/\s+/g, ' ').trim();
  const settle = async () => {
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    devices = [
      {
        id: 'd1',
        name: 'X250',
        createdAt: '2026-11-01T09:00:00Z',
        lastUsedAt: '2026-11-12T08:30:00Z',
      },
      {
        id: 'd2',
        name: 'Librem 14',
        createdAt: '2026-11-05T09:00:00Z',
        lastUsedAt: null,
      },
    ];

    await TestBed.configureTestingModule({
      imports: [CarrelDevicesSettingsComponent],
      providers: [
        { provide: CarrelService, useValue: api },
        { provide: ToastService, useValue: toast },
        provideRouter([]),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CarrelDevicesSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  });

  afterEach(() => fixture.destroy());

  it('lists the paired devices and when each last synced', () => {
    const items = all('carrel-devices-item');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('X250');
    expect(items[0].textContent).toContain('last synced');
    expect(items[1].textContent).toContain('Librem 14');
    expect(items[1].textContent).toContain('not synced yet');
    expect(el('carrel-devices-pair')?.getAttribute('href')).toBe('/pair');
  });

  it('says so when no device is paired yet', async () => {
    devices = [];
    component.load();
    await settle();

    expect(text('carrel-devices-empty')).toContain('No devices paired yet');
  });

  it('asks before revoking, then removes the device', async () => {
    el('carrel-devices-revoke')!.click();
    await settle();
    expect(api.revoke).not.toHaveBeenCalled();
    expect(component.pendingRevoke()).toEqual(devices[0]);

    component.confirmRevoke();
    await settle();

    expect(api.revoke).toHaveBeenCalledWith('d1');
    expect(all('carrel-devices-item')).toHaveLength(1);
    expect(component.pendingRevoke()).toBeNull();
    expect(toast.success).toHaveBeenCalledWith('X250 can no longer sync');
  });

  it('lets the person cancel the revoke', async () => {
    component.requestRevoke(devices[0]);
    component.cancelRevoke();
    await settle();

    expect(component.pendingRevoke()).toBeNull();
    expect(api.revoke).not.toHaveBeenCalled();
  });

  it('keeps a device whose revoke failed', async () => {
    api.revoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.requestRevoke(devices[0]);
    component.confirmRevoke();
    await settle();

    expect(all('carrel-devices-item')).toHaveLength(2);
    expect(toast.error).toHaveBeenCalledWith('Could not revoke X250');
  });

  it('treats an already revoked device as revoked', async () => {
    api.revoke.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 404 })));
    component.requestRevoke(devices[0]);
    component.confirmRevoke();
    await settle();

    expect(all('carrel-devices-item')).toHaveLength(1);
    expect(toast.success).toHaveBeenCalledWith('X250 can no longer sync');
  });

  it('offers a retry when the list does not load', async () => {
    api.devices.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.load();
    await settle();

    expect(fixture.nativeElement.textContent).toContain('Could not load your devices');
    expect(component.loadFailed()).toBe(true);
  });
});
