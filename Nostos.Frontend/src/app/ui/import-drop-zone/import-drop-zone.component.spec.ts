import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter, Router } from '@angular/router';
import { Subject, throwError, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportDropZoneComponent } from './import-drop-zone.component';
import { storedZip } from './test-zip';
import { HighlightImportService } from '../../core/services/highlight-import.service';
import { ReadingHistoryImportService } from '../../core/services/reading-history-import.service';
import { CalibreImportService } from '../../core/services/calibre-import.service';
import { PortableArchiveHandoffService } from '../../core/services/portable-archive-handoff.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { createDeploymentCapabilitiesServiceMock } from '../../core/services/deployment-capabilities.testing';
import { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';

const GOODREADS_HEADER =
  'Book Id,Title,Author,ISBN13,My Rating,Bookshelves,Exclusive Shelf,My Review,Read Count';
const STORYGRAPH_HEADER =
  'Title,Authors,ISBN/UID,Format,Read Status,Last Date Read,Star Rating,Review,Tags';
const READWISE_HEADER = 'Highlight,Book Title,Book Author,Note,Tags,Location Type,Location';

const selfHosted = {
  deploymentMode: 'SelfHosted' as const,
  requiresAuthentication: false,
  canConfigureAiProvider: true,
  managedAi: false,
  managedVoiceTranscription: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsManagedBackups: false,
  supportsPrivateNetworkAccess: true,
  supportsEreaderAccess: true,
  usageMeteringAvailable: false,
  supportsLibraryMigration: true,
  supportsCalibreImport: true,
  supportsCalibreUpload: true,
};

function csv(headers: string, name: string): File {
  return new File([`${headers}\nA Book,An Author`], name);
}

function binaryFile(name: string): File {
  return new File([new Uint8Array([0x53, 0x51, 0x4c, 0x69, 0x74, 0x65])], name);
}

function archiveFile(): File {
  return new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04])], 'library.nostos');
}

function dropEvent(files: File[]): Event {
  const event = new Event('drop', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'dataTransfer', { value: { files, types: ['Files'] } });
  return event;
}

describe('ImportDropZoneComponent', () => {
  let fixture: ComponentFixture<ImportDropZoneComponent>;
  let component: ImportDropZoneComponent;
  let highlight: HighlightImportService;
  let history: ReadingHistoryImportService;
  let handoff: PortableArchiveHandoffService;
  let calibre: CalibreImportService;
  let router: Router;

  const button = () =>
    fixture.nativeElement.querySelector('[data-testid="import-drop-zone-button"]') as HTMLButtonElement;
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  async function drop(files: File[]): Promise<void> {
    button().dispatchEvent(dropEvent(files));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ImportDropZoneComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: DeploymentCapabilitiesService,
          useValue: createDeploymentCapabilitiesServiceMock(() => of(selfHosted)),
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ImportDropZoneComponent);
    component = fixture.componentInstance;
    highlight = TestBed.inject(HighlightImportService);
    history = TestBed.inject(ReadingHistoryImportService);
    handoff = TestBed.inject(PortableArchiveHandoffService);
    calibre = TestBed.inject(CalibreImportService);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true as never);
    fixture.detectChanges();
  });

  it('is a real button with the one instruction, and opening the picker is a click', () => {
    expect(button().tagName).toBe('BUTTON');
    expect(button().type).toBe('button');
    expect(button().textContent).toContain('Drop an export here, or choose a file');

    const input = fixture.nativeElement.querySelector(
      '[data-testid="import-drop-zone-input"]',
    ) as HTMLInputElement;
    expect(input.getAttribute('aria-hidden')).toBe('true');
    expect(input.tabIndex).toBe(-1);

    const click = vi.spyOn(input, 'click');
    button().click();
    expect(click).toHaveBeenCalled();
  });

  it('shows the drag-over state and clears it when the file leaves', () => {
    const dragOver = new Event('dragenter', { bubbles: true, cancelable: true });
    Object.defineProperty(dragOver, 'dataTransfer', { value: { types: ['Files'] } });
    button().dispatchEvent(dragOver);
    fixture.detectChanges();
    expect(button().classList.contains('is-dragging')).toBe(true);

    button().dispatchEvent(new Event('dragleave', { bubbles: true }));
    fixture.detectChanges();
    expect(button().classList.contains('is-dragging')).toBe(false);
  });

  it('imports every highlight source by its real file, with no source chosen first', async () => {
    const open = vi.spyOn(highlight, 'openWithFiles');
    const historyOpen = vi.spyOn(history, 'openWithFile');
    const files = [
      binaryFile('KoboReader.sqlite'),
      new File(['return { annotations = {} }'], 'metadata.lua'),
      new File(['A Book\n- Your Highlight on page 1'], 'My Clippings.txt'),
      binaryFile('AEAnnotation_v10312011_1727_local.sqlite'),
      csv(READWISE_HEADER, 'readwise-export.csv'),
    ];

    for (const file of files) {
      await drop([file]);
      await vi.waitFor(() => expect(open).toHaveBeenLastCalledWith([file]));
      await vi.waitFor(() => expect(component.routing()).toBe(false));
    }
    expect(open).toHaveBeenCalledTimes(files.length);
    expect(historyOpen).not.toHaveBeenCalled();
  });

  it('imports a Goodreads and a StoryGraph history by their real file', async () => {
    const open = vi.spyOn(history, 'openWithFile');
    const highlightOpen = vi.spyOn(highlight, 'openWithFiles');
    const files = [
      csv(GOODREADS_HEADER, 'goodreads_library_export.csv'),
      csv(STORYGRAPH_HEADER, 'storygraph_export.csv'),
    ];

    for (const file of files) {
      await drop([file]);
      await vi.waitFor(() => expect(open).toHaveBeenLastCalledWith(file));
      await vi.waitFor(() => expect(component.routing()).toBe(false));
    }
    expect(open).toHaveBeenCalledTimes(2);
    expect(highlightOpen).not.toHaveBeenCalled();
  });

  it('hands a dropped .nostos archive to the whole-library import', async () => {
    const file = archiveFile();
    await drop([file]);
    await vi.waitFor(() => expect(router.navigate).toHaveBeenCalled());

    expect(handoff.pending()).toBe(file);
    expect(router.navigate).toHaveBeenCalledWith(['/settings/library'], {
      queryParams: { action: 'import' },
    });
  });

  it('says what is supported and links to each help line for an unrecognised file', async () => {
    vi.spyOn(history, 'preview').mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 400, error: { error: 'This file is not an export.' } })),
    );
    vi.spyOn(highlight, 'preview').mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { error: 'This file is not one Nostos can import highlights from.' },
          }),
      ),
    );

    await drop([new File(['%PDF-1.7'], 'book.pdf')]);
    await vi.waitFor(() =>
      expect(fixture.nativeElement.querySelector('[data-testid="import-drop-zone-notice"]')).not.toBeNull(),
    );
    fixture.detectChanges();

    expect(text()).toContain('Nostos can import these files');
    expect(text()).toContain('not one Nostos can import highlights from');
    for (const id of ['kobo', 'koreader', 'kindle', 'applebooks', 'readwise', 'goodreads', 'storygraph', 'archive']) {
      expect(fixture.nativeElement.querySelector(`[data-testid="import-supported-${id}"]`)).not.toBeNull();
    }

    (
      fixture.nativeElement.querySelector('[data-testid="import-supported-kobo"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    const summary = fixture.nativeElement.querySelector(
      '[data-testid="import-help-kobo"] [aria-expanded]',
    ) as HTMLButtonElement;
    expect(summary.getAttribute('aria-expanded')).toBe('true');
    expect(
      (fixture.nativeElement.querySelector('[data-testid="import-help-kobo"]') as HTMLElement).textContent,
    ).toContain('KoboReader.sqlite');
  });

  it('lists the help for every source, one line each, expandable', () => {
    for (const id of ['kobo', 'koreader', 'kindle', 'applebooks', 'readwise', 'goodreads', 'storygraph', 'archive', 'calibre']) {
      const entry = fixture.nativeElement.querySelector(`[data-testid="import-help-${id}"]`) as HTMLElement;
      expect(entry, id).toBeTruthy();
      const summary = entry.querySelector('[aria-expanded]') as HTMLButtonElement;
      expect(summary.getAttribute('aria-expanded')).toBe('false');
      summary.click();
      fixture.detectChanges();
      expect(summary.getAttribute('aria-expanded')).toBe('true');
    }
    expect(text()).toContain('My Books → Import and export');
    expect(text()).toContain('Manage Account → Export StoryGraph Library');
  });

  it('names Calibre among the files the zone takes when it accepts the zip', () => {
    expect(text()).toContain('StoryGraph, Calibre or a .nostos archive');
  });

  it('sends a dropped Calibre zip to the Calibre upload, file and all', async () => {
    const openWithFile = vi.spyOn(calibre, 'openWithFile');
    const highlightOpen = vi.spyOn(highlight, 'openWithFiles');
    const historyOpen = vi.spyOn(history, 'openWithFile');
    const zip = storedZip(
      [{ name: 'library/metadata.db' }, { name: 'library/books/1.epub' }],
      'calibre-library.zip',
    );

    await drop([zip]);
    await vi.waitFor(() => expect(openWithFile).toHaveBeenCalledWith(zip));

    expect(highlightOpen).not.toHaveBeenCalled();
    expect(historyOpen).not.toHaveBeenCalled();
  });

  it('opens the Calibre import from its help line, as the whole-library action it is', () => {
    const open = vi.spyOn(calibre, 'open');
    const help = fixture.nativeElement.querySelector('[data-testid="import-help"]') as HTMLElement;
    const groups = Array.from(help.querySelectorAll('.import-help__group')).map((group) =>
      (group.textContent ?? '').trim(),
    );
    expect(groups).toContain('Whole library');

    const entry = fixture.nativeElement.querySelector('[data-testid="import-help-calibre"]') as HTMLElement;
    (entry.querySelector('[aria-expanded]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (entry.querySelector('[data-testid="import-help-calibre-open"]') as HTMLButtonElement).click();
    expect(open).toHaveBeenCalled();
  });

  it('opens the Apple Books folder steps from its help line', () => {
    const open = vi.spyOn(highlight, 'open');
    const entry = fixture.nativeElement.querySelector('[data-testid="import-help-applebooks"]') as HTMLElement;
    (entry.querySelector('[aria-expanded]') as HTMLButtonElement).click();
    fixture.detectChanges();

    (entry.querySelector('[data-testid="import-help-applebooks-open"]') as HTMLButtonElement).click();
    expect(open).toHaveBeenCalledWith('applebooks');
  });

  it('reactively reveals archive and Calibre help lines once capabilities emit', async () => {
    const pending = new Subject<DeploymentCapabilities>();
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [ImportDropZoneComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: DeploymentCapabilitiesService,
          useValue: createDeploymentCapabilitiesServiceMock(() => pending.asObservable()),
        },
      ],
    }).compileComponents();

    const reactiveFixture = TestBed.createComponent(ImportDropZoneComponent);
    reactiveFixture.detectChanges();

    expect(
      reactiveFixture.nativeElement.querySelector('[data-testid="import-help-archive"]'),
    ).toBeNull();
    expect(
      reactiveFixture.nativeElement.querySelector('[data-testid="import-help-calibre"]'),
    ).toBeNull();

    pending.next(selfHosted);
    reactiveFixture.detectChanges();

    expect(
      reactiveFixture.nativeElement.querySelector('[data-testid="import-help-archive"]'),
    ).not.toBeNull();
    expect(
      reactiveFixture.nativeElement.querySelector('[data-testid="import-help-calibre"]'),
    ).not.toBeNull();
  });
});
