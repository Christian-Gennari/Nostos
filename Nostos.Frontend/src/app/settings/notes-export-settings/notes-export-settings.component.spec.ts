import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse, HttpHeaders, HttpResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { PortableLibraryService } from '../../core/services/portable-library.service';
import { ToastService } from '../../core/services/toast.service';
import { NotesExportSettingsComponent } from './notes-export-settings.component';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };

const portableLibraryServiceMock = {
  exportArchive: vi.fn(),
  exportNotesMarkdown: vi.fn(),
};

describe('NotesExportSettingsComponent', () => {
  let fixture: ComponentFixture<NotesExportSettingsComponent>;
  let component: NotesExportSettingsComponent;

  async function configure(): Promise<void> {
    TestBed.resetTestingModule();
portableLibraryServiceMock.exportNotesMarkdown.mockClear();
    portableLibraryServiceMock.exportNotesMarkdown.mockReturnValue(
      of(
        new HttpResponse({
          body: new Blob(['zip']),
          headers: new HttpHeaders({
            'content-disposition': 'attachment; filename="nostos-notes-2026-10-24.zip"',
          }),
        }),
      ),
    );
    toastMock.success.mockClear();
    toastMock.error.mockClear();

    await TestBed.configureTestingModule({
      imports: [NotesExportSettingsComponent],
      providers: [
        { provide: PortableLibraryService, useValue: portableLibraryServiceMock },
        { provide: ToastService, useValue: toastMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(NotesExportSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('exports the notes with writings included by default', async () => {
    await configure();
    const saveSpy = vi
      .spyOn(component as unknown as { saveDownload: (blob: Blob, name: string) => void }, 'saveDownload')
      .mockImplementation(() => {});

    (
      fixture.nativeElement.querySelector('[data-testid="markdown-notes-export-action"]') as HTMLButtonElement
    ).click();

    expect(portableLibraryServiceMock.exportNotesMarkdown).toHaveBeenCalledWith(true);
    expect(saveSpy).toHaveBeenCalledWith(expect.any(Blob), 'nostos-notes-2026-10-24.zip');
    expect(component.notesExportBusy()).toBe(false);
    expect(toastMock.success).toHaveBeenCalledWith('Your notes are ready.');
  });

  it('leaves writings out when the box is cleared', async () => {
    await configure();
    vi.spyOn(
      component as unknown as { saveDownload: (blob: Blob, name: string) => void },
      'saveDownload',
    ).mockImplementation(() => {});

    const writings = fixture.nativeElement.querySelector(
      '[data-testid="markdown-notes-export-writings"]',
    ) as HTMLInputElement;
    writings.click();
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-testid="markdown-notes-export-action"]') as HTMLButtonElement
    ).click();

    expect(portableLibraryServiceMock.exportNotesMarkdown).toHaveBeenCalledWith(false);
    expect(writings.checked).toBe(false);
  });

  it('says plainly when the notes export fails', async () => {
    await configure();
    portableLibraryServiceMock.exportNotesMarkdown.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server error' })),
    );

    component.exportNotesAsMarkdown();
    fixture.detectChanges();

    expect(component.notesExportBusy()).toBe(false);
    expect(component.notesExportError()).toBe(
      'Nostos could not export your notes. Nothing was changed. Try again.',
    );
  });

  it('names a session that no longer allows the export instead of blaming the server', async () => {
    await configure();
    portableLibraryServiceMock.exportNotesMarkdown.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 401 })),
    );

    component.exportNotesAsMarkdown();

    expect(component.notesExportError()).toBe(
      'Your session no longer allows this export. Sign in again, then retry.',
    );
  });

  it('reports an empty export rather than downloading nothing', async () => {
    await configure();
    portableLibraryServiceMock.exportNotesMarkdown.mockReturnValue(
      of(new HttpResponse({ body: new Blob([]) })),
    );

    component.exportNotesAsMarkdown();

    expect(component.notesExportError()).toBe('Nostos returned an empty export. Try again.');
  });
});
