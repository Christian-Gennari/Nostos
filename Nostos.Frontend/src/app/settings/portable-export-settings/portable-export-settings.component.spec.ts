import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { HttpErrorResponse, HttpHeaders, HttpResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { CloudEntryService } from '../../core/services/cloud-entry.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { createDeploymentCapabilitiesServiceMock } from '../../core/services/deployment-capabilities.testing';
import { PortableLibraryService } from '../../core/services/portable-library.service';
import { ToastService } from '../../core/services/toast.service';
import { PortableExportSettingsComponent } from './portable-export-settings.component';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };

const portableLibraryServiceMock = {
  exportArchive: vi.fn(),
  exportNotesMarkdown: vi.fn(),
};

const cloudEntryServiceMock = {
  actionPending: signal(false),
  actionError: signal<string | null>(null),
  firstRunImportPending: signal(false),
  importPortableArchive: vi.fn(async () => {}),
};

describe('PortableExportSettingsComponent', () => {
  let fixture: ComponentFixture<PortableExportSettingsComponent>;
  let component: PortableExportSettingsComponent;

  async function configure(inputs: {
    isManageLibraryPage?: boolean;
    isFirstRunImport?: boolean;
    isImportSelected?: boolean;
  } = {}): Promise<void> {
    TestBed.resetTestingModule();
portableLibraryServiceMock.exportArchive.mockClear();
    portableLibraryServiceMock.exportArchive.mockReturnValue(
      of(
        new HttpResponse({
          body: new Blob(['portable']),
          headers: new HttpHeaders({
            'content-disposition': 'attachment; filename="nostos-export-test.nostos"',
          }),
        }),
      ),
    );
    cloudEntryServiceMock.importPortableArchive.mockClear();
    toastMock.success.mockClear();
    toastMock.error.mockClear();

    await TestBed.configureTestingModule({
      imports: [PortableExportSettingsComponent],
      providers: [
        { provide: PortableLibraryService, useValue: portableLibraryServiceMock },
        { provide: CloudEntryService, useValue: cloudEntryServiceMock },
        { provide: ToastService, useValue: toastMock },
        {
          provide: DeploymentCapabilitiesService,
          useValue: createDeploymentCapabilitiesServiceMock({
            deploymentMode: 'Cloud',
            requiresAuthentication: true,
            canConfigureAiProvider: false,
            managedAi: true,
            managedVoiceTranscription: true,
            usesCloudStorage: true,
            supportsLocalBackupConfiguration: false,
            supportsManagedBackups: true,
            supportsPrivateNetworkAccess: false,
            supportsEreaderAccess: true,
            usageMeteringAvailable: false,
            accountManagementUrl: null,
            feedbackUrl: null,
            supportsLibraryMigration: false,
          }),
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PortableExportSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isManageLibraryPage', inputs.isManageLibraryPage ?? true);
    fixture.componentRef.setInput('isFirstRunImport', inputs.isFirstRunImport ?? false);
    fixture.componentRef.setInput('isImportSelected', inputs.isImportSelected ?? false);
    fixture.detectChanges();
  }

  it('offers the first-run import block only on the Manage library route', async () => {
    await configure({ isManageLibraryPage: true, isFirstRunImport: true });
    expect(fixture.nativeElement.querySelector('[data-testid="first-run-cloud-import"]')).not.toBeNull();

    fixture.destroy();
    await configure({ isManageLibraryPage: false, isFirstRunImport: true });
    expect(fixture.nativeElement.querySelector('[data-testid="first-run-cloud-import"]')).toBeNull();
  });

  it('offers the first-run import to a browser that has one pending', async () => {
    cloudEntryServiceMock.firstRunImportPending.set(true);
    await configure({ isManageLibraryPage: true });

    expect(fixture.nativeElement.querySelector('[data-testid="first-run-cloud-import"]')).not.toBeNull();
  });

  it('marks the import block when the page was opened on it', async () => {
    await configure({ isManageLibraryPage: true, isFirstRunImport: true, isImportSelected: true });

    expect(
      fixture.nativeElement
        .querySelector('[data-testid="first-run-cloud-import"]')
        .classList.contains('is-selected'),
    ).toBe(true);
  });

  it('downloads the archive once and reports it', async () => {
    await configure();

    component.exportAllNostosData();

    expect(portableLibraryServiceMock.exportArchive).toHaveBeenCalledTimes(1);
    expect(component.portableExportBusy()).toBe(false);
    expect(toastMock.success).toHaveBeenCalledWith('Your Nostos export is ready');
  });

  it('stops the busy state and says why when the export fails', async () => {
    await configure();
    portableLibraryServiceMock.exportArchive.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server error' })),
    );

    component.exportAllNostosData();
    fixture.detectChanges();

    expect(component.portableExportError()).toBe(
      'Nostos could not create the export. Your data was not changed. Try again.',
    );
    expect(toastMock.error).toHaveBeenCalled();
  });

  it('hands a picked archive to the Cloud entry service and clears the input', async () => {
    await configure();
    const input = { files: [new File(['x'], 'library.nostos')], value: 'picked' };

    await component.importFirstRunArchive({
      target: input,
    } as unknown as Event);

    expect(cloudEntryServiceMock.importPortableArchive).toHaveBeenCalledWith(input.files[0]);
    expect(input.value).toBe('');
  });
});
