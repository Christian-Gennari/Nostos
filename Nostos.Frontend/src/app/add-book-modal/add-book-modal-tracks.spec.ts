import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { Book, BooksService } from '../core/services/books.service';
import { BookUploadService } from '../core/services/book-upload.service';
import { ToastService } from '../core/services/toast.service';

/** Several audio files chosen together become one audiobook (#1373). */
describe('AddBookModal — several audio files', () => {
  let component: AddBookModal;
  let fixture: ComponentFixture<AddBookModal>;

  const audio = (name: string, size = 10) =>
    new File([new Uint8Array(size)], name, { type: 'audio/mpeg' });

  /** jsdom has no DataTransfer, so the input is a stand-in with a file list. */
  function choose(files: File[]): { value: string } {
    const input = { files, value: 'chosen' };
    component.onFileSelected({ target: input } as unknown as Event);
    return input;
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AddBookModal] }).compileComponents();
    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', []);
    await fixture.whenStable();
  });

  it('treats two or more audio files as the tracks of one audiobook', () => {
    choose([audio('02.mp3', 20), audio('01.mp3', 30)]);

    expect(component.form.type).toBe('audiobook');
    expect(component.trackFiles().length).toBe(2);
    expect(component.fileSummary()).toContain('2 audio files');
    expect(component.form.title).toBe('');
  });

  it('keeps a single chosen file a single file', () => {
    choose([audio('book.mp3')]);

    expect(component.trackFiles().length).toBe(0);
    expect(component.selectedFile()?.name).toBe('book.mp3');
    expect(component.form.title).toBe('book');
  });

  it('refuses several files when any of them is not audio', () => {
    const toast = TestBed.inject(ToastService);
    const info = vi.spyOn(toast, 'info');

    const input = choose([
      audio('1.mp3'),
      new File(['x'], 'b.epub', { type: 'application/epub+zip' }),
    ]);

    expect(component.selectedFile()).toBeNull();
    expect(component.trackFiles().length).toBe(0);
    expect(input.value).toBe('');
    expect(info).toHaveBeenCalled();
  });

  it('creates the book once and sends every file in one tracks request', () => {
    const books = TestBed.inject(BooksService);
    const createSpy = vi
      .spyOn(books, 'createOrMatch')
      .mockReturnValue(of({ book: { id: 'new-book' } as unknown as Book, created: true }));
    const tracksSpy = vi.spyOn(books, 'uploadTracks').mockReturnValue(new Subject<any>());
    const singleSpy = vi.spyOn(books, 'uploadFile');
    const resumable = vi.spyOn(TestBed.inject(BookUploadService), 'upload');

    choose([audio('2.mp3'), audio('1.mp3')]);
    component.form.title = 'My Audiobook';
    component.submit();

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(tracksSpy).toHaveBeenCalledTimes(1);
    expect(tracksSpy.mock.calls[0][0]).toBe('new-book');
    expect(tracksSpy.mock.calls[0][1].map((f) => f.name)).toEqual(['2.mp3', '1.mp3']);
    expect(singleSpy).not.toHaveBeenCalled();
    expect(resumable).not.toHaveBeenCalled();
  });
});
