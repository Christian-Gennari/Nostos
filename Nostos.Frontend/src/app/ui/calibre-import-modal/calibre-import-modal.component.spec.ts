import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { of } from 'rxjs';

import { CalibreImportModal } from './calibre-import-modal.component';
import {
  CalibreFormatChoice,
  CalibreImportJobState,
  CalibrePreview,
  CalibrePreviewBook,
  CalibreImportService,
  CalibreStatus,
  CalibreUploadStatus,
} from '../../core/services/calibre-import.service';
import {
  CalibreUploadError,
  CalibreUploadService,
} from '../../core/services/calibre-upload.service';

const format = (overrides: Partial<CalibreFormatChoice> = {}): CalibreFormatChoice => ({
  key: 'calibre:1:EPUB',
  format: 'EPUB',
  fileName: 'Pride and Prejudice - Jane Austen.epub',
  bytes: 1527,
  kind: 'ebook',
  alreadyHere: false,
  existingBookId: null,
  selectedByDefault: true,
  ...overrides,
});

const book = (overrides: Partial<CalibrePreviewBook> = {}): CalibrePreviewBook => ({
  sourceKey: 'calibre:1',
  calibreId: 1,
  title: 'Pride and Prejudice',
  author: 'Jane Austen',
  isbn: '9780141439518',
  series: null,
  volumeNumber: null,
  publisher: 'T. Egerton',
  publishedDate: '1813-01-28',
  rating: 5,
  description: 'A novel.',
  hasCover: true,
  tags: ['classics', 'romance'],
  readAt: '2020-05-01T00:00:00Z',
  readNotice: null,
  match: 'exact',
  bookId: 'b1',
  candidates: [
    { bookId: 'b1', title: 'Pride and Prejudice', author: 'Jane Austen', reason: 'Same ISBN', format: 'ebook' },
  ],
  formats: [format(), format({ key: 'calibre:1:PDF', format: 'PDF', fileName: 'book.pdf', selectedByDefault: false })],
  unsupportedFormats: [],
  existingTitle: 'Pride and Prejudice',
  notice: null,
  ...overrides,
});

const preview = (books: CalibrePreviewBook[]): CalibrePreview => ({
  source: 'calibre',
  summary: {
    books: books.length,
    ready: books.filter((entry) => entry.match !== 'suggested').length,
    inLibrary: books.filter((entry) => entry.match === 'exact').length,
    alreadyHere: 0,
    needsReview: books.filter((entry) => entry.match === 'suggested').length,
    skipped: 1,
    formats: 3,
    unsupportedFormats: 1,
    truncated: false,
  },
  books,
  skipped: [{ title: 'The Art of War', author: 'Sun Tzu', count: 1, reason: 'Nostos cannot read MOBI, AZW3.' }],
  customColumns: [
    { label: 'read', name: 'Read', dataType: 'bool', status: 'used', reason: 'Sets whether the book was read.' },
    { label: 'genre', name: 'Genre', dataType: 'text', status: 'skipped', reason: 'Nostos has no field for this column.' },
  ],
  warnings: [],
});

const importing: CalibreImportJobState = {
  status: 'importing',
  startedAt: '2026-11-11T10:00:00Z',
  finishedAt: null,
  total: 1,
  completed: 0,
  current: 'Pride and Prejudice',
  batchId: null,
  results: [],
  error: null,
};

describe('CalibreImportModal', () => {
  let fixture: ComponentFixture<CalibreImportModal>;
  let component: CalibreImportModal;
  let service: CalibreImportService;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  const open = async (
    status: { configured: boolean; available: boolean } & Partial<CalibreStatus>,
    books: CalibrePreviewBook[],
  ) => {
    vi.spyOn(service, 'status').mockReturnValue(of({
      import: { ...importing, status: 'idle', total: 0, current: null },
      folderSupported: true,
      uploadSupported: false,
      maxUploadBytes: null,
      upload: null,
      ...status,
    } as CalibreStatus));
    vi.spyOn(service, 'preview').mockReturnValue(of(preview(books)));
    service.open();
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CalibreImportModal],
      providers: [provideHttpClient()],
    }).compileComponents();

    fixture = TestBed.createComponent(CalibreImportModal);
    component = fixture.componentInstance;
    service = TestBed.inject(CalibreImportService);
    vi.spyOn(service, 'recent').mockReturnValue(of([]));
  });

  it('renders nothing until the service opens it', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.modal-card')).toBeNull();
  });

  it('explains how to give the server a library instead of showing an empty review', async () => {
    await open({ configured: false, available: false }, []);

    expect(component.stage()).toBe('setup');
    expect(text()).toContain('Calibre__LibraryRoot');
    expect(text()).toContain('/external/calibre');
    expect(text()).toContain('only ever read');
  });

  it('reviews every book, chooses the best format and keeps the other formats available', async () => {
    await open({ configured: true, available: true }, [book()]);

    expect(component.stage()).toBe('review');
    expect(text()).toContain('Review before importing');
    expect(text()).toContain('Pride and Prejudice');
    expect(text()).toContain('Jane Austen');
    expect(text()).toContain('classics');
    expect(text()).toContain('#read');
    expect(text()).toContain('#genre');
    expect(component.selectedFormats()).toBe(1);
    expect(component.selectedBooks()).toBe(1);
    expect(text()).toContain('Import 1 book, 1 file');

    const pdf = component.rows()[0].book.formats[1];
    component.toggleFormat(component.rows()[0], pdf);
    fixture.detectChanges();

    expect(component.selectedFormats()).toBe(2);
    expect(text()).toContain('Import 1 book, 2 files');
  });

  it('sends the ticked formats and the collections answer to the server', async () => {
    await open({ configured: true, available: true }, [book()]);
    vi.spyOn(service, 'start').mockReturnValue(of(importing));
    vi.spyOn(service, 'progress').mockReturnValue(of(importing));

    await component.importNow();

    expect(service.start).toHaveBeenCalledWith([
      {
        sourceKey: 'calibre:1',
        formats: ['calibre:1:EPUB'],
        bookId: undefined,
        createCollections: false,
      },
    ]);
    expect(component.stage()).toBe('importing');
    component.close();
  });

  it('offers the zip upload on a host without a folder and goes to review when it lands', async () => {
    const uploadService = TestBed.inject(CalibreUploadService);
    const staged: CalibreUploadStatus = {
      sessionId: 'up-1',
      state: 'staged',
      fileName: 'library.zip',
      totalBytes: 1_000_000,
      partBytes: 16 * 1024 * 1024,
      totalParts: 1,
      receivedParts: [0],
      receivedBytes: 1_000_000,
      createdAtUtc: '2026-11-11T10:00:00Z',
      expiresAtUtc: '2026-11-12T10:00:00Z',
      maxLibraryBytes: 8 * 1024 * 1024 * 1024,
      maxExtractedBytes: 16 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      books: 1,
      stagedBytes: 2_000_000,
      failureCode: null,
      failureMessage: null,
    };
    const idle: CalibreStatus = {
      configured: false,
      available: false,
      import: { ...importing, status: 'idle', total: 0, current: null },
      folderSupported: false,
      uploadSupported: true,
      maxUploadBytes: 8 * 1024 * 1024 * 1024,
      upload: null,
    };
    vi.spyOn(service, 'status')
      .mockReturnValueOnce(of(idle))
      .mockReturnValue(of({ ...idle, available: true, configured: true, upload: staged }));
    vi.spyOn(service, 'preview').mockReturnValue(of(preview([book()])));
    vi.spyOn(uploadService, 'upload').mockResolvedValue(staged);

    service.open();
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();

    expect(component.stage()).toBe('upload');
    expect(fixture.nativeElement.querySelector('[data-testid="calibre-upload-picker"]')).toBeTruthy();
    expect(text()).toContain('Up to 8.0 GB');

    const file = new File([new Uint8Array([1, 2, 3])], 'library.zip', { type: 'application/zip' });
    await component.onLibraryFile({ target: { files: [file], value: 'library.zip' } } as unknown as Event);
    fixture.detectChanges();

    expect(uploadService.upload).toHaveBeenCalledWith(
      file,
      expect.objectContaining({ maxBytes: 8 * 1024 * 1024 * 1024 }),
    );
    expect(component.stage()).toBe('review');
    expect(text()).toContain('Review before importing');
  });

  it('keeps a partial upload visible for resuming and cancels it explicitly', async () => {
    const uploadService = TestBed.inject(CalibreUploadService);
    const partial: CalibreUploadStatus = {
      sessionId: 'up-2',
      state: 'receiving',
      fileName: 'library.zip',
      totalBytes: 40_000_000,
      partBytes: 16 * 1024 * 1024,
      totalParts: 3,
      receivedParts: [0],
      receivedBytes: 16 * 1024 * 1024,
      createdAtUtc: '2026-11-11T10:00:00Z',
      expiresAtUtc: '2026-11-12T10:00:00Z',
      maxLibraryBytes: 8 * 1024 * 1024 * 1024,
      maxExtractedBytes: 16 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      books: null,
      stagedBytes: null,
      failureCode: null,
      failureMessage: null,
    };
    await open(
      {
        configured: false,
        available: false,
        folderSupported: false,
        uploadSupported: true,
        upload: partial,
        maxUploadBytes: partial.maxLibraryBytes,
      },
      [],
    );
    const cancel = vi.spyOn(uploadService, 'cancel').mockResolvedValue();

    expect(component.stage()).toBe('upload');
    expect(text()).toContain('Choose the same zip to resume');
    expect(text()).toContain('16.0 MB of 38.1 MB');

    await component.cancelUpload();
    expect(cancel).toHaveBeenCalledWith('up-2');
    expect(component.currentUpload()).toBeNull();
  });

  it('shows the server reason when a zip is refused', async () => {
    const uploadService = TestBed.inject(CalibreUploadService);
    await open(
      { configured: false, available: false, folderSupported: false, uploadSupported: true },
      [],
    );
    vi.spyOn(uploadService, 'upload').mockRejectedValue(
      new CalibreUploadError(
        'calibre_upload_unsafe_entry',
        422,
        'The zip contains a symbolic link. Create it without links.',
      ),
    );

    const file = new File([new Uint8Array([1])], 'library.zip', { type: 'application/zip' });
    await component.onLibraryFile({ target: { files: [file], value: 'library.zip' } } as unknown as Event);
    fixture.detectChanges();

    expect(component.stage()).toBe('upload');
    expect(text()).toContain('The zip contains a symbolic link');
  });

  it('asks before undoing and reports what was kept', async () => {
    await open({ configured: true, available: true }, [book()]);
    component.stage.set('result');
    component.results.set([{
      sourceKey: 'calibre:1',
      calibreId: 1,
      title: 'Pride and Prejudice',
      status: 'imported',
      bookId: 'b1',
      bookTitle: 'Pride and Prejudice',
      fileCount: 1,
      created: true,
      message: null,
    }]);
    component.batchId.set('batch-1');
    vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 1, kept: 1 }));
    fixture.detectChanges();

    component.askUndoImport();
    expect(component.pendingUndo()).not.toBeNull();
    await component.confirmUndo();
    fixture.detectChanges();

    expect(service.undo).toHaveBeenCalledWith('batch-1');
    expect(text()).toContain('Import undone');
    expect(text()).toContain('1 book stayed');
  });
});
