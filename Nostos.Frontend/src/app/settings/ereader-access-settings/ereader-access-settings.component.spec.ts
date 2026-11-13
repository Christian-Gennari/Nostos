import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ManagedOpdsAccess, OpdsInfo } from '../../core/dtos/opds.dtos';
import { OpdsService } from '../../core/services/opds.service';
import { ToastService } from '../../core/services/toast.service';
import { EreaderAccessSettingsComponent } from './ereader-access-settings.component';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };

const remoteInfo: OpdsInfo = {
  enabled: true,
  catalogUrl: 'https://omenhub.example.ts.net:5215/opds/',
  urlSource: 'request',
  localOnly: false,
};

const managedDisabled: ManagedOpdsAccess = {
  enabled: false,
  username: null,
  password: null,
  createdAtUtc: null,
  rotatedAtUtc: null,
  revokedAtUtc: null,
};

const managedEnabled: ManagedOpdsAccess = {
  enabled: true,
  username: 'reader-example',
  password: null,
  createdAtUtc: '2026-09-24T17:00:00Z',
  rotatedAtUtc: '2026-09-24T17:00:00Z',
  revokedAtUtc: null,
};

const opdsServiceMock = {
  getInfo: vi.fn<() => Observable<OpdsInfo>>(),
  getManagedAccess: vi.fn<() => Observable<ManagedOpdsAccess>>(),
  enableManagedAccess: vi.fn<() => Observable<ManagedOpdsAccess>>(),
  rotateManagedPassword: vi.fn<() => Observable<ManagedOpdsAccess>>(),
  revokeManagedAccess: vi.fn<() => Observable<ManagedOpdsAccess>>(),
};

describe('EreaderAccessSettingsComponent', () => {
  let fixture: ComponentFixture<EreaderAccessSettingsComponent>;
  let component: EreaderAccessSettingsComponent;

  function installClipboard(writeText: (text: string) => Promise<void>): void {
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });
  }

  async function configure(managed = false, privateNetwork = true): Promise<void> {
    TestBed.resetTestingModule();
await TestBed.configureTestingModule({
      imports: [EreaderAccessSettingsComponent],
      providers: [
        { provide: OpdsService, useValue: opdsServiceMock },
        { provide: ToastService, useValue: toastMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(EreaderAccessSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('managed', managed);
    fixture.componentRef.setInput('privateNetwork', privateNetwork);
    fixture.detectChanges();
  }

  beforeEach(() => {
    opdsServiceMock.getInfo.mockClear();
    opdsServiceMock.getInfo.mockReturnValue(of(remoteInfo));
    opdsServiceMock.getManagedAccess.mockClear();
    opdsServiceMock.getManagedAccess.mockReturnValue(of(managedDisabled));
    opdsServiceMock.enableManagedAccess.mockClear();
    // The plaintext secret is one-time response material; this spec only cares
    // that enabling leaves access enabled with a username.
    opdsServiceMock.enableManagedAccess.mockReturnValue(
      of({ ...managedDisabled, enabled: true, username: 'reader-example', password: null }),
    );
    opdsServiceMock.rotateManagedPassword.mockClear();
    opdsServiceMock.rotateManagedPassword.mockReturnValue(
      of({ ...managedDisabled, enabled: true, username: 'reader-example', password: null }),
    );
    opdsServiceMock.revokeManagedAccess.mockClear();
    toastMock.error.mockClear();
    toastMock.success.mockClear();
  });

  afterEach(() => {
    delete (navigator as { clipboard?: unknown }).clipboard;
    vi.useRealTimers();
  });

  it('loads the catalogue address itself, so the card can be mounted on its own', async () => {
    await configure();

    expect(opdsServiceMock.getInfo).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('.catalog-url')?.textContent.trim()).toBe(
      remoteInfo.catalogUrl,
    );
  });

  it('reads managed credentials only when the managed model is the one in use', async () => {
    await configure(false);
    expect(opdsServiceMock.getManagedAccess).not.toHaveBeenCalled();

    fixture.destroy();
    await configure(true, false);

    expect(opdsServiceMock.getManagedAccess).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('[data-testid="managed-opds-disabled"]')).not.toBeNull();
  });

  it('copies the address and clears the Copied state after the feedback delay', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    installClipboard(writeText);
    await configure();

    vi.useFakeTimers();
    (fixture.nativeElement.querySelector('.catalog-url-row button') as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(remoteInfo.catalogUrl);
    expect(component.copied()).toBe(true);

    vi.advanceTimersByTime(2_500);
    fixture.detectChanges();
    expect(component.copied()).toBe(false);
  });

  it('leaves no copied-feedback timer running after the section is destroyed', async () => {
    installClipboard(vi.fn(() => Promise.resolve()));
    await configure();

    vi.useFakeTimers();
    (fixture.nativeElement.querySelector('.catalog-url-row button') as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    expect(component.copied()).toBe(true);

    fixture.destroy();
    vi.advanceTimersByTime(2_500);

    // The feedback timer was cancelled with the component: nothing reset it.
    expect(component.copied()).toBe(true);
  });

  it('asks before regenerating the managed password, then applies the new one', async () => {
    opdsServiceMock.getManagedAccess.mockReturnValue(of(managedEnabled));
    await configure(true, false);

    const rotate = fixture.nativeElement.querySelector(
      '[data-testid="rotate-managed-opds-password"]',
    ) as HTMLButtonElement;
    rotate.click();
    fixture.detectChanges();

    expect(opdsServiceMock.rotateManagedPassword).not.toHaveBeenCalled();
    expect(component.pendingOpdsRotate()).toBe(true);

    component.rotateManagedOpdsPassword();
    fixture.detectChanges();

    expect(opdsServiceMock.rotateManagedPassword).toHaveBeenCalledTimes(1);
    expect(component.pendingOpdsRotate()).toBe(false);
  });
});
