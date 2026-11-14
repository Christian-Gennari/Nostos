import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';

import { CalibreImportService } from '../../core/services/calibre-import.service';
import { ReadwiseService } from '../../core/services/readwise.service';
import { ImportSettingsComponent } from './import-settings.component';

const readwiseServiceMock = {
  status: vi.fn(() => of({ connected: false, lastImportedAtUtc: null, importedThroughUtc: null })),
  connect: vi.fn(),
  disconnect: vi.fn(),
  preview: vi.fn(),
  commit: vi.fn(),
};

const calibreImportServiceMock = {
  isOpen: signal(false),
  open: vi.fn(),
};

const routerMock = { navigate: vi.fn(), events: of([]) };

describe('ImportSettingsComponent', () => {
  let fixture: ComponentFixture<ImportSettingsComponent>;

  async function configure(mediaFolders: boolean): Promise<void> {
    TestBed.resetTestingModule();
await TestBed.configureTestingModule({
      imports: [ImportSettingsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: routerMock },
        { provide: ReadwiseService, useValue: readwiseServiceMock },
        { provide: CalibreImportService, useValue: calibreImportServiceMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ImportSettingsComponent);
    fixture.componentRef.setInput('mediaFolders', mediaFolders);
    fixture.detectChanges();
  }

  it('always offers the shared drop zone', async () => {
    await configure(false);

    expect(fixture.nativeElement.querySelector('[data-testid="import-drop-card"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-import-drop-zone')).not.toBeNull();
  });

  it('offers the media-folder row only when the server reads a folder', async () => {
    await configure(false);
    expect(fixture.nativeElement.querySelector('[data-testid="import-books-card"]')).toBeNull();

    fixture.destroy();
    await configure(true);

    expect(fixture.nativeElement.querySelector('[data-testid="import-books-card"]')).not.toBeNull();
  });

  it('asks the page to open External media instead of routing itself', async () => {
    await configure(true);
    const emitted: string[] = [];
    fixture.componentInstance.openSection.subscribe((section) => emitted.push(section));

    (
      fixture.nativeElement.querySelector('[data-testid="import-action-folders"]') as HTMLButtonElement
    ).click();

    expect(emitted).toEqual(['external']);
  });
});
