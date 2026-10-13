import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import {
  ExternalMediaImportState,
  ExternalMediaScanItem,
  ExternalMediaScanResponse,
  ExternalMediaScanSummary,
  ExternalMediaService,
  ExternalMediaStatus,
} from '../core/services/external-media.service';
import {
  EXTERNAL_MEDIA_PROMISE,
  ExternalMediaSettingsComponent,
} from './external-media-settings.component';

describe('ExternalMediaSettingsComponent', () => {
  let fixture: ComponentFixture<ExternalMediaSettingsComponent>;
  let component: ExternalMediaSettingsComponent;

  const idle = { status: 'idle', startedAt: null, finishedAt: null, examined: 0, total: 0, error: null } as const;
  const done = { ...idle, status: 'done', finishedAt: '2026-10-13T10:00:00Z', examined: 4, total: 4 } as const;
  const summary: ExternalMediaScanSummary = {
    discovered: 4,
    ready: 2,
    alreadyLinked: 0,
    needsReview: 1,
    unusable: 1,
    unsupportedFiles: 1,
    missingBooks: 0,
    skippedLinks: 2,
    unreadableFolders: 0,
    unusableNames: 0,
    truncated: false,
  };
  const item = (
    key: string,
    status: ExternalMediaScanItem['status'],
    concerns: string[] = [],
  ): ExternalMediaScanItem => ({
    key,
    kind: 'audiobook',
    layout: 'tracks',
    location: key.replace('tracks:', ''),
    status,
    title: key.split('/').pop()!,
    author: 'Jane Author',
    narrator: null,
    language: null,
    fileCount: 12,
    totalBytes: 1024 * 1024 * 300,
    concerns,
    linkedBookId: null,
    libraryMatches: [],
  });
  const items = [
    item('tracks:Jane Author/First', 'ready'),
    item('tracks:Jane Author/Second', 'ready'),
    item('tracks:Jane Author/Twin', 'review', ['A book with this title is already in your library.']),
    item('tracks:Jane Author/Broken', 'unusable', ['The first file could not be read as audio.']),
  ];
  const noImport: ExternalMediaImportState = {
    status: 'idle',
    startedAt: null,
    finishedAt: null,
    total: 0,
    completed: 0,
    current: null,
    outcomes: [],
  };

  let status: ExternalMediaStatus;
  let scanned: ExternalMediaScanSummary | null;
  let importState: ExternalMediaImportState;

  const api = {
    status: vi.fn(() => of(status)),
    startScan: vi.fn(() => {
      scanned = summary;
      status = { ...status, scan: done };
      return of(done);
    }),
    scan: vi.fn((wanted?: string) => {
      const matching = scanned ? items.filter((i) => !wanted || i.status === wanted) : [];
      const response: ExternalMediaScanResponse = {
        scan: status.scan,
        summary: scanned,
        items: matching,
        matchingItems: matching.length,
        unsupportedSamples: scanned ? ['Other/Album.flac'] : [],
        missing: [],
      };
      return of(response);
    }),
    startImport: vi.fn(() => {
      importState = { ...noImport, status: 'importing', total: 2, current: 'First' };
      return of(importState);
    }),
    importState: vi.fn(() => of(importState)),
  };

  const text = (testId: string) =>
    (fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null)?.textContent
      ?.replace(/\s+/g, ' ')
      .trim();
  const click = (testId: string) =>
    (fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
  /** The component chains plain promises, which `whenStable` does not wait for. */
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    status = { mounted: true, linkedBooks: 0, scan: idle };
    scanned = null;
    importState = noImport;

    await TestBed.configureTestingModule({
      imports: [ExternalMediaSettingsComponent],
      providers: [{ provide: ExternalMediaService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(ExternalMediaSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  });

  afterEach(() => fixture.destroy());

  it('states the promise about original files before anything is scanned', () => {
    expect(text('external-media-folder-row')).toContain(EXTERNAL_MEDIA_PROMISE);
    expect(text('external-media-mounted')).toBe('Available');
    expect(text('external-media-scan')).toBe('Scan folder');
    expect(fixture.nativeElement.querySelector('[data-testid="external-media-review"]')).toBeNull();
  });

  it('scans, then summarises what was found and offers the review', async () => {
    click('external-media-scan');
    await settle();

    expect(api.startScan).toHaveBeenCalledTimes(1);
    expect(text('external-media-scan-summary')).toBe(
      'Last scan found 4 books: 2 ready, 1 to check, 0 already in Nostos.',
    );
    expect(text('external-media-scan')).toBe('Scan again');
    expect(text('external-media-review')).toBe('Review and import');
  });

  it('reviews every group before importing and says what was passed over', async () => {
    await component.startScan();
    await component.openReview();
    fixture.detectChanges();

    const tally = Array.from(
      fixture.nativeElement.querySelectorAll('[data-testid="external-media-tally"] li') as NodeListOf<HTMLElement>,
    ).map((entry) => entry.textContent?.trim());
    expect(tally).toEqual(['2 ready', '1 to check', '0 already in Nostos', '1 cannot be imported']);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="external-media-ready-row"]').length).toBe(2);
    expect(text('external-media-review-row')).toContain('already in your library');
    expect(text('external-media-unusable-row')).toContain('could not be read as audio');
    expect(text('external-media-skipped')).toContain('2 symbolic links (never followed)');
    expect(fixture.nativeElement.textContent).toContain('Other/Album.flac');
    expect(text('external-media-import')).toBe('Import 2 books');
  });

  it('imports every ready book except the ones unticked, plus the reviewed ones ticked', async () => {
    await component.startScan();
    await component.openReview();
    fixture.detectChanges();

    component.toggle(items[1]);
    component.toggle(items[2]);
    fixture.detectChanges();
    expect(text('external-media-import')).toBe('Import 2 books');

    click('external-media-import');
    await settle();

    expect(api.startImport).toHaveBeenCalledWith({
      allReady: true,
      except: ['tracks:Jane Author/Second'],
      keys: ['tracks:Jane Author/Twin'],
    });
    expect(text('external-media-progress')).toContain('Importing 0 of 2');
    expect(text('external-media-progress')).toContain('no file is copied or changed');
  });

  it('cannot import when nothing is ticked', async () => {
    await component.startScan();
    await component.openReview();
    component.toggle(items[0]);
    component.toggle(items[1]);
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('[data-testid="external-media-import"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it('shows the result and scans again once the server finishes an import', async () => {
    await component.startScan();
    await component.openReview();
    await component.importSelected();
    api.startScan.mockClear();

    importState = {
      ...noImport,
      status: 'done',
      total: 2,
      completed: 2,
      outcomes: [
        { key: 'a', title: 'First', outcome: 'imported', bookId: '1', message: null },
        { key: 'b', title: 'Second', outcome: 'failed', bookId: null, message: 'A file is no longer in the folder. Scan again.' },
      ],
    };
    await component.refresh();
    fixture.detectChanges();

    expect(text('external-media-result')).toContain('1 book added to your library');
    expect(fixture.nativeElement.textContent).toContain('A file is no longer in the folder. Scan again.');
    expect(api.startScan).toHaveBeenCalledTimes(1);
    expect(text('external-media-import-row')).toContain('1 book added, 1 not imported.');
  });

  it('picks up an import that is already running when the page is opened', async () => {
    importState = { ...noImport, status: 'importing', total: 40, completed: 12, current: 'A Novel' };
    await component.refresh();
    fixture.detectChanges();

    expect(text('external-media-import-row')).toContain('Importing 12 of 40');
    expect(text('external-media-import-row')).toContain('A Novel');
    const scan = fixture.nativeElement.querySelector('[data-testid="external-media-scan"]') as HTMLButtonElement;
    expect(scan.disabled).toBe(true);
  });

  it('explains an unmounted folder and does not offer a scan', async () => {
    status = { mounted: false, linkedBooks: 3, scan: idle };
    await component.refresh();
    fixture.detectChanges();

    expect(text('external-media-mounted')).toBe('Not available');
    expect(text('external-media-unmounted')).toContain('Linked books keep their notes and progress');
    expect(text('external-media-scan-row')).toContain('3 linked books');
    const scan = fixture.nativeElement.querySelector('[data-testid="external-media-scan"]') as HTMLButtonElement;
    expect(scan.disabled).toBe(true);
  });

  it('says that books with missing files were kept', async () => {
    scanned = { ...summary, missingBooks: 2 };
    status = { ...status, scan: done };
    await component.refresh();
    fixture.detectChanges();

    expect(text('external-media-missing-notice')).toContain('2 linked books have files that are no longer in the folder');
    expect(text('external-media-missing-notice')).toContain('Nothing was removed from Nostos');
  });

  it("shows the server's reason when a scan cannot start", async () => {
    api.startScan.mockReturnValueOnce(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { code: 'external_media_not_mounted', message: 'The external media folder is not available.' },
          }),
      ),
    );

    await component.startScan();
    fixture.detectChanges();

    expect(text('external-media-error')).toBe('The external media folder is not available.');
  });
});
