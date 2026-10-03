import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { NoteSearchHit } from '../../core/dtos/note.dtos';
import { NoteCaptureDetailsComponent } from './note-capture-details.component';

describe('NoteCaptureDetailsComponent', () => {
  let http: HttpTestingController;
  const note: NoteSearchHit = {
    id: 'captured-note', bookId: 'book', bookTitle: 'A book', content: 'Polished thought.',
    selectedText: 'An exact quotation.', snippet: null, topicNames: [], createdAt: '2026-10-01',
    captureSource: 'voice', processingMode: 'light_polish', hasRawContent: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [NoteCaptureDetailsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  function render(value = note) {
    const fixture = TestBed.createComponent(NoteCaptureDetailsComponent);
    fixture.componentRef.setInput('note', value);
    fixture.detectChanges();
    return fixture;
  }

  it('adds no controls or provider requests to an ordinary manual/imported note', () => {
    const fixture = render({ ...note, hasRawContent: false, captureSource: 'import' });
    expect(fixture.nativeElement.textContent.trim()).toBe('');
    http.expectNone(() => true);
  });

  it('shows effective capture mode but fetches original wording only when opened', () => {
    const fixture = render();
    expect(fixture.nativeElement.textContent).toContain('Light polish · Spoken thought');
    http.expectNone(() => true);
    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="note-original-text"]')).toBeNull();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({
      id: note.id, rawContent: 'so i think <b>this</b> matters\nmaybe',
      content: note.content, processingMode: 'light_polish',
    });
    fixture.detectChanges();
    const original = fixture.nativeElement.querySelector('[data-testid="note-original-text"]');
    expect(original.textContent).toBe('so i think <b>this</b> matters\nmaybe');
    expect(original.querySelector('b')).toBeNull();
    expect(original.closest('[role="dialog"]').getAttribute('aria-label')).toBe('Original wording');
    expect(fixture.nativeElement.querySelector('[data-testid="note-capture-details"] [data-testid="note-original-text"]')).toBeNull();
    fixture.componentInstance.closeOriginal();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeNull();
    fixture.componentInstance.openOriginal();
    http.expectNone(() => true);
  });

  it('restores through the canonical endpoint and emits the committed note', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    const emitted = vi.fn();
    fixture.componentInstance.restored.subscribe(emitted);
    fixture.componentInstance.restoreOriginal();
    fixture.componentInstance.restoreOriginal();
    const request = http.expectOne('/api/notes/captured-note/raw/restore');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({});
    const saved = { ...note, content: 'raw words', processingMode: 'verbatim', rawContent: 'raw words' };
    request.flush(saved);
    expect(emitted).toHaveBeenCalledExactlyOnceWith(saved);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Original restored');
  });

  it('reports a failed restore without presenting a success and permits retry', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    const emitted = vi.fn();
    fixture.componentInstance.restored.subscribe(emitted);
    fixture.componentInstance.restoreOriginal();
    http.expectOne('/api/notes/captured-note/raw/restore').flush({}, { status: 502, statusText: 'Failed' });
    fixture.detectChanges();
    expect(emitted).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Could not confirm the restore');
    expect(fixture.componentInstance.restoring()).toBe(false);
    fixture.componentInstance.restoreOriginal();
    http.expectOne('/api/notes/captured-note/raw/restore').flush({ ...note, content: 'raw words' });
  });

  it('permits retry after an original-text load failure', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Could not load the original wording');
    fixture.componentInstance.closeOriginal();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    expect(fixture.componentInstance.original()).toBe('raw words');
  });

  it('allows closing while loading and reuses the pending request on reopening', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    const pending = http.expectOne('/api/notes/captured-note/raw');
    fixture.componentInstance.closeOriginal();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeNull();
    fixture.componentInstance.openOriginal();
    http.expectNone(() => true);
    pending.flush({ id: note.id, rawContent: 'raw words' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="note-original-text"]').textContent).toBe('raw words');
  });

  it('locks modal dismissal while a restore is in flight', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    fixture.componentInstance.restoreOriginal();
    fixture.componentInstance.closeOriginal();
    expect(fixture.componentInstance.originalOpen()).toBe(true);
    http.expectOne('/api/notes/captured-note/raw/restore').flush({ ...note, content: 'raw words' });
    fixture.componentInstance.closeOriginal();
    expect(fixture.componentInstance.originalOpen()).toBe(false);
  });

  it('discards a delayed original response after selecting a different note', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    const pending = http.expectOne('/api/notes/captured-note/raw');
    fixture.componentRef.setInput('note', { ...note, id: 'another-note' });
    fixture.detectChanges();
    pending.flush({ id: note.id, rawContent: 'wrong note words' });
    expect(fixture.componentInstance.original()).toBeNull();
    expect(fixture.componentInstance.originalOpen()).toBe(false);
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/another-note/raw').flush({ id: 'another-note', rawContent: 'correct words' });
  });

  it('reconciles a committed restore by id without populating a newly selected note', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    const emitted = vi.fn();
    fixture.componentInstance.restored.subscribe(emitted);
    fixture.componentInstance.restoreOriginal();
    const pending = http.expectOne('/api/notes/captured-note/raw/restore');
    fixture.componentRef.setInput('note', { ...note, id: 'another-note' });
    fixture.detectChanges();
    pending.flush({ ...note, content: 'raw words', processingMode: 'verbatim' });
    expect(emitted).toHaveBeenCalledOnce();
    expect(emitted.mock.calls[0][0].id).toBe(note.id);
    expect(fixture.componentInstance.original()).toBeNull();
    expect(fixture.componentInstance.restoring()).toBe(false);
  });

  it('keeps the original open when canonical data for the same note refreshes', () => {
    const fixture = render();
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    fixture.componentRef.setInput('note', { ...note, content: 'raw words', processingMode: 'verbatim' });
    fixture.detectChanges();
    expect(fixture.componentInstance.original()).toBe('raw words');
    expect(fixture.componentInstance.originalOpen()).toBe(true);
  });

  it('keeps the original available after reload and restore, with effective mode verbatim', () => {
    const fixture = render({ ...note, processingMode: 'verbatim', content: 'raw words' });
    expect(fixture.nativeElement.textContent).toContain('Verbatim');
    fixture.componentInstance.openOriginal();
    http.expectOne('/api/notes/captured-note/raw').flush({ id: note.id, rawContent: 'raw words' });
    fixture.detectChanges();
    const restore = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((button) => (button as HTMLButtonElement).textContent?.includes('Restore original')) as HTMLButtonElement;
    expect(restore.disabled).toBe(true);
  });
});
