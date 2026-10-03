import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';

import { BrainWritingHandoffComponent } from './brain-writing-handoff.component';
import { WritingDto, WritingSourceDto } from '../../core/dtos/writing.dtos';
import { ToastService } from '../../core/services/toast.service';

const documentWriting: WritingDto = {
  id: 'writing-1',
  name: 'Solitude and freedom',
  type: 'Document',
  parentId: null,
  updatedAt: '2026-09-25T06:00:00Z',
};

const folderWriting: WritingDto = {
  id: 'folder-1',
  name: 'Draft folders',
  type: 'Folder',
  parentId: null,
  updatedAt: '2026-09-25T06:00:00Z',
};

const source = (id: string): WritingSourceDto => ({
  id,
  bookId: 'book-1',
  bookTitle: 'Walden',
  content: 'A canonical note',
  selectedText: null,
  cfiRange: null,
  createdAt: '2026-09-20T06:00:00Z',
  addedAt: '2026-09-25T06:00:00Z',
  sourceAnchorKind: 'None',
  sourceAnchorValue: null,
  anchorVerified: false,
});

describe('BrainWritingHandoffComponent', () => {
  let fixture: ComponentFixture<BrainWritingHandoffComponent>;
  let component: BrainWritingHandoffComponent;
  let http: HttpTestingController;

  const create = async (noteIds: string[] = ['note-1']): Promise<void> => {
    fixture = TestBed.createComponent(BrainWritingHandoffComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('noteIds', noteIds);
    fixture.detectChanges();
    http = TestBed.inject(HttpTestingController);

    http.expectOne('/api/writings').flush([folderWriting, documentWriting]);
    fixture.detectChanges();
    await fixture.whenStable();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BrainWritingHandoffComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  afterEach(() => {
    http?.verify();
  });

  it('requires an explicit destination and excludes folders', async () => {
    await create();

    const keep = fixture.nativeElement.querySelector('.handoff-actions .primary') as HTMLButtonElement;
    expect(keep.disabled).toBe(true);

    const destinations = [
      ...fixture.nativeElement.querySelectorAll('.handoff-option.existing'),
    ] as HTMLButtonElement[];
    expect(destinations.map((button) => button.textContent?.trim())).toEqual([
      'Solitude and freedom',
    ]);
    expect(fixture.nativeElement.textContent).not.toContain('Draft folders');

    destinations[0].click();
    fixture.detectChanges();
    expect(keep.disabled).toBe(false);
  });

  it.each([
    ['named', 'Notes toward an essay', 'Notes toward an essay'],
    ['blank', '   ', 'Untitled writing'],
  ])('creates a new root Document with a %s title', async (_label, enteredTitle, expectedTitle) => {
    await create();
    const completed = vi.fn();
    component.completed.subscribe(completed);

    component.chooseNew();
    component.newWritingTitle.set(enteredTitle);
    component.confirm();

    const createRequest = http.expectOne('/api/writings');
    expect(createRequest.request.method).toBe('POST');
    expect(createRequest.request.body).toEqual({
      name: expectedTitle,
      type: 'Document',
      parentId: null,
    });
    createRequest.flush({
      id: 'writing-new',
      name: expectedTitle,
      type: 'Document',
      parentId: null,
      updatedAt: '2026-09-25T06:00:00Z',
    });

    http.expectOne('/api/writings/writing-new/notes').flush([]);
    http.expectOne((request) =>
      request.method === 'POST' &&
      request.url === '/api/writings/writing-new/notes' &&
      request.body.noteId === 'note-1'
    ).flush(source('note-1'));

    expect(completed).toHaveBeenCalledWith({
      succeededNoteIds: ['note-1'],
      failedNoteIds: [],
    });
  });

  it('deduplicates note IDs and quietly skips sources already kept', async () => {
    await create(['note-1', 'note-1', 'note-2']);
    const completed = vi.fn();
    component.completed.subscribe(completed);

    component.chooseExisting(documentWriting.id);
    component.confirm();

    http.expectOne('/api/writings/writing-1/notes').flush([source('note-1')]);

    const adds = http.match(
      (request) => request.method === 'POST' && request.url === '/api/writings/writing-1/notes'
    );
    expect(adds.length).toBe(1);
    expect(adds[0].request.body).toEqual({ noteId: 'note-2' });
    adds[0].flush(source('note-2'));

    expect(completed).toHaveBeenCalledWith({
      succeededNoteIds: ['note-1', 'note-2'],
      failedNoteIds: [],
    });
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain('1 already there');
  });

  it('composes one idempotent addSource call per missing source', async () => {
    await create(['note-1', 'note-2', 'note-3']);
    component.chooseExisting(documentWriting.id);
    component.confirm();

    http.expectOne('/api/writings/writing-1/notes').flush([]);

    const adds = http.match(
      (request) => request.method === 'POST' && request.url === '/api/writings/writing-1/notes'
    );
    expect(adds.map((request) => request.request.body.noteId).sort()).toEqual([
      'note-1',
      'note-2',
      'note-3',
    ]);
    adds.forEach((request) => request.flush(source(request.request.body.noteId)));
  });

  it('reports partial failure without rolling back successful memberships', async () => {
    await create(['note-1', 'note-2']);
    const completed = vi.fn();
    component.completed.subscribe(completed);

    component.chooseExisting(documentWriting.id);
    component.confirm();

    http.expectOne('/api/writings/writing-1/notes').flush([]);
    const adds = http.match(
      (request) => request.method === 'POST' && request.url === '/api/writings/writing-1/notes'
    );
    expect(adds.length).toBe(2);

    const first = adds.find((request) => request.request.body.noteId === 'note-1')!;
    const second = adds.find((request) => request.request.body.noteId === 'note-2')!;
    first.flush(source('note-1'));
    second.error(new ProgressEvent('network-error'));

    expect(completed).toHaveBeenCalledWith({
      succeededNoteIds: ['note-1'],
      failedNoteIds: ['note-2'],
    });
    expect(component.saving()).toBe(false);
    expect(TestBed.inject(ToastService).toasts().at(-1)?.message).toContain(
      '1 source could not be added'
    );
  });

  it('cancels without any writing or membership mutation', async () => {
    await create();
    const cancelled = vi.fn();
    component.cancelled.subscribe(cancelled);

    component.cancel();

    expect(cancelled).toHaveBeenCalledTimes(1);
    http.expectNone((request) => request.method !== 'GET');
  });

  it('navigates to the exact writingId contract only when explicitly requested', async () => {
    await create();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    component.chooseExisting(documentWriting.id);
    component.openWritingAfterAdd.set(true);
    component.confirm();

    http.expectOne('/api/writings/writing-1/notes').flush([]);
    http.expectOne((request) =>
      request.method === 'POST' &&
      request.url === '/api/writings/writing-1/notes' &&
      request.body.noteId === 'note-1'
    ).flush(source('note-1'));

    expect(navigate).toHaveBeenCalledWith('/studio?writingId=writing-1');
  });

  it('does not navigate by default', async () => {
    await create();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    component.chooseExisting(documentWriting.id);
    component.confirm();
    http.expectOne('/api/writings/writing-1/notes').flush([source('note-1')]);

    expect(component.openWritingAfterAdd()).toBe(false);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('keeps the same destination and actions available at a narrow viewport', async () => {
    const previousWidth = window.innerWidth;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 });
    window.dispatchEvent(new Event('resize'));

    await create();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.handoff-dialog')).toBeTruthy();
    expect(fixture.nativeElement.querySelectorAll('.handoff-option.existing').length).toBe(1);
    expect(fixture.nativeElement.querySelector('.handoff-actions .primary')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.handoff-actions .secondary')).toBeTruthy();

    Object.defineProperty(window, 'innerWidth', { configurable: true, value: previousWidth });
  });

  it('uses only writing membership APIs and never mutates canonical Brain notes', async () => {
    await create();
    component.chooseExisting(documentWriting.id);
    component.confirm();

    http.expectOne('/api/writings/writing-1/notes').flush([]);
    http.expectOne('/api/writings/writing-1/notes').flush(source('note-1'));

    http.expectNone((request) =>
      request.url.startsWith('/api/notes') || request.url.startsWith('/api/topics')
    );
  });
});
