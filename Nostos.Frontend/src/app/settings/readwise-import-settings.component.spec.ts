import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ReadwiseImportSettingsComponent } from './readwise-import-settings.component';
import { HighlightImportService } from '../core/services/highlight-import.service';

const disconnected = { connected: false, lastImportedAtUtc: null, importedThroughUtc: null };
const connected = {
  connected: true,
  lastImportedAtUtc: '2026-11-01T10:00:00Z',
  importedThroughUtc: '2026-11-01T09:00:00Z',
};

describe('ReadwiseImportSettingsComponent', () => {
  let fixture: ComponentFixture<ReadwiseImportSettingsComponent>;
  let http: HttpTestingController;
  let highlightImport: HighlightImportService;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReadwiseImportSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(ReadwiseImportSettingsComponent);
    http = TestBed.inject(HttpTestingController);
    highlightImport = TestBed.inject(HighlightImportService);
  });

  afterEach(() => http.verify());

  it('shows Connect with a token beside the CSV fallback when disconnected', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(disconnected);
    fixture.detectChanges();

    expect(text()).toContain('Connect with a token');
    expect(text()).toContain('Import CSV');
    expect(fixture.nativeElement.querySelector('[data-testid="readwise-import-now"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="import-source-readwise"]')).not.toBeNull();
  });

  it('checks and saves a token, then offers Import now without showing the token again', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(disconnected);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="readwise-connect"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector(
      '[data-testid="readwise-token-input"]',
    ) as HTMLInputElement;
    input.value = '  a-reader-token  ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="readwise-token-save"]') as HTMLButtonElement).click();

    const save = http.expectOne('/api/notes/imports/readwise/connect');
    expect(save.request.method).toBe('POST');
    expect(save.request.body).toEqual({ token: 'a-reader-token' });
    save.flush(connected);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="readwise-token-input"]')).toBeNull();
    expect(text()).toContain('Connected');
    expect(text()).toContain('Last import');
    expect(fixture.nativeElement.querySelector('[data-testid="readwise-import-now"]')).not.toBeNull();
  });

  it('opens the normal highlight import on Import now', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(connected);
    fixture.detectChanges();
    const open = vi.spyOn(highlightImport, 'open');

    (fixture.nativeElement.querySelector('[data-testid="readwise-import-now"]') as HTMLButtonElement).click();

    expect(open).toHaveBeenCalledWith('readwise');
  });

  it('keeps the CSV path and opens the file picker', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(disconnected);
    fixture.detectChanges();
    const open = vi.spyOn(highlightImport, 'open');

    (fixture.nativeElement.querySelector('[data-testid="import-action-readwise"]') as HTMLButtonElement).click();

    expect(open).toHaveBeenCalledWith('all');
  });

  it('disconnects and removes the Connected state', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(connected);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="readwise-disconnect"]') as HTMLButtonElement).click();
    const disconnect = http.expectOne('/api/notes/imports/readwise');
    expect(disconnect.request.method).toBe('DELETE');
    disconnect.flush(disconnected);
    fixture.detectChanges();

    expect(text()).toContain('Connect with a token');
    expect(fixture.nativeElement.querySelector('[data-testid="readwise-import-now"]')).toBeNull();
  });

  it('shows the server message when Readwise rejects the token', () => {
    fixture.detectChanges();
    http.expectOne('/api/notes/imports/readwise').flush(disconnected);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="readwise-connect"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('[data-testid="readwise-token-input"]') as HTMLInputElement;
    input.value = 'bad-token';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="readwise-token-save"]') as HTMLButtonElement).click();

    http
      .expectOne('/api/notes/imports/readwise/connect')
      .flush({ error: 'Readwise did not accept that access token.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="readwise-error"]')?.textContent).toContain(
      'Readwise did not accept that access token.',
    );
  });
});
