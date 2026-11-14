import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Observable, of } from 'rxjs';

import { CloudSession } from '../../core/dtos/cloud-auth.dtos';
import { CloudAccountDeletionStatus } from '../../core/dtos/cloud-account-deletion.dtos';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
import { CloudAccountDeletionService } from '../../core/services/cloud-account-deletion.service';
import { CloudAccountDeletionSettingsComponent } from '../cloud-account-deletion-settings.component';
import { CloudAccountSettingsComponent } from './cloud-account-settings.component';

const session: CloudSession = {
  authenticated: true,
  accountState: 'Active',
  account: {
    id: '1e4df713-1a34-4fc7-9a90-c45169256845',
    displayName: 'Reader',
    email: 'reader@example.test',
  },
};

const cloudAuthServiceMock = {
  getSession: vi.fn<() => Observable<CloudSession>>(),
  logout: vi.fn(),
};

const deletionStatus: CloudAccountDeletionStatus = {
  state: 'GracePeriod',
  gracePeriodDays: 14,
  requestedAtUtc: '2026-10-01T12:00:00Z',
  eligibleAtUtc: '2026-10-15T12:00:00Z',
  completedAtUtc: null,
  canCancel: true,
  portableExportUrl: '/api/portability/export',
};

const cloudAccountDeletionServiceMock = {
  getStatus: vi.fn(() => of(deletionStatus)),
  requestDeletion: vi.fn(() => of(deletionStatus)),
  cancelDeletion: vi.fn(() => of({ ...deletionStatus, state: 'Cancelled' })),
};

describe('CloudAccountSettingsComponent', () => {
  let fixture: ComponentFixture<CloudAccountSettingsComponent>;
  let component: CloudAccountSettingsComponent;

  async function configure(inputs: {
    accountManagementUrl?: string | null;
    supportsAccountDeletion?: boolean;
  } = {}): Promise<void> {
    TestBed.resetTestingModule();
cloudAuthServiceMock.getSession.mockClear();
    cloudAuthServiceMock.getSession.mockReturnValue(of(session));
    cloudAuthServiceMock.logout.mockClear();

    await TestBed.configureTestingModule({
      imports: [CloudAccountSettingsComponent],
      providers: [
        { provide: CloudAuthService, useValue: cloudAuthServiceMock },
        { provide: CloudAccountDeletionService, useValue: cloudAccountDeletionServiceMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CloudAccountSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput(
      'accountManagementUrl',
      inputs.accountManagementUrl ?? null,
    );
    fixture.componentRef.setInput(
      'supportsAccountDeletion',
      inputs.supportsAccountDeletion ?? false,
    );
    fixture.detectChanges();
  }

  it('reads the BFF session and shows the signed-in identity', async () => {
    await configure();

    expect(cloudAuthServiceMock.getSession).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.textContent).toContain('Reader');
    expect(fixture.nativeElement.textContent).toContain('reader@example.test');
  });

  it('signs out through the BFF', async () => {
    await configure();

    (
      fixture.nativeElement.querySelector('button') as HTMLButtonElement
    ).click();

    expect(cloudAuthServiceMock.logout).toHaveBeenCalledTimes(1);
  });

  it('links account and billing to the account site the host names', async () => {
    await configure({ accountManagementUrl: 'https://nostos.page/account' });

    const link = fixture.nativeElement.querySelector(
      '[data-testid="cloud-account-management-link"]',
    ) as HTMLAnchorElement;
    expect(link.href).toBe('https://nostos.page/account');
    expect(link.rel).toBe('noopener noreferrer');
  });

  it('shows the danger zone only when the host advertises deletion', async () => {
    await configure({ supportsAccountDeletion: false });
    expect(
      fixture.nativeElement.querySelector('[data-testid="cloud-account-danger-zone"]'),
    ).toBeNull();

    fixture.destroy();
    await configure({ supportsAccountDeletion: true });
    expect(
      fixture.nativeElement.querySelector('[data-testid="cloud-account-danger-zone"]'),
    ).not.toBeNull();
  });

  it('passes a started deletion up to the page, which owns the pending view', async () => {
    await configure({ supportsAccountDeletion: true });
    const pending: CloudAccountDeletionStatus[] = [];
    component.deletionPending.subscribe((status) => pending.push(status));

    fixture.debugElement
      .query(By.directive(CloudAccountDeletionSettingsComponent))
      .componentInstance.pending.emit(deletionStatus);

    expect(pending).toEqual([deletionStatus]);
  });
});
