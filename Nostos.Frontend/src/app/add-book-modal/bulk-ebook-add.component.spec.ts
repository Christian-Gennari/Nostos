import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HttpEventType } from '@angular/common/http';

import { BulkEbookAddComponent, titleFromFileName } from './bulk-ebook-add.component';
import { Book, BooksService } from '../core/services/books.service';
import { BookUploadService } from '../core/services/book-upload.service';

const file = (name: string) => new File(['x'], name);
const book = (over: Partial<Book>) => ({ id: 'b1', title: 'T', hasFile: false, ...over }) as unknown as Book;

describe('BulkEbookAddComponent', () => {
  let fixture: ComponentFixture<BulkEbookAddComponent>;
  let component: BulkEbookAddComponent;
  let books: BooksService;

  async function setup(files: File[]) {
    await TestBed.configureTestingModule({ imports: [BulkEbookAddComponent] }).compileComponents();
    books = TestBed.inject(BooksService);
    vi.spyOn(TestBed.inject(BookUploadService), 'supportsResumable').mockReturnValue(false);
    vi.spyOn(books, 'readFileMetadata').mockImplementation((f) =>
      of(
        f.name.startsWith('broken')
          ? { title: null, author: null, language: null, pageCount: null }
          : { title: `Title of ${f.name}`, author: 'An Author', language: 'sv', pageCount: 1 },
      ),
    );
    fixture = TestBed.createComponent(BulkEbookAddComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('files', files);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('builds a name-based title and replaces it with what each file says', async () => {
    await setup([file('a_b-c.epub'), file('broken.pdf')]);
    const rows = component.rows();
    expect(rows[0]).toMatchObject({ title: 'Title of a_b-c.epub', author: 'An Author', status: 'ready' });
    expect(rows[1]).toMatchObject({ title: 'broken', author: '', status: 'ready' });
    expect(titleFromFileName('the_magic-mountain.epub')).toBe('the magic mountain');
  });

  it('creates and uploads each file in turn, skips a duplicate with a note, and summarises', async () => {
    await setup([file('one.epub'), file('two.epub'), file('three.pdf')]);
    const create = vi
      .spyOn(books, 'createOrMatch')
      .mockReturnValueOnce(of({ book: book({ id: '1' }), created: true }))
      .mockReturnValueOnce(of({ book: book({ id: '2', title: 'Two', hasFile: true }), created: false }))
      .mockReturnValueOnce(throwError(() => new Error('boom')));
    const upload = vi
      .spyOn(books, 'uploadFile')
      .mockReturnValue(of({ type: HttpEventType.Response } as never));

    component.edit(0, 'title', 'Edited one');
    await component.start();

    expect(create).toHaveBeenCalledTimes(3);
    expect(create.mock.calls[0][0]).toMatchObject({
      type: 'ebook',
      title: 'Edited one',
      author: 'An Author',
      language: 'Swedish',
      fileAttached: true,
      fileUploadExpected: true,
    });
    expect(upload).toHaveBeenCalledTimes(1);
    expect(component.rows().map((r) => r.status)).toEqual(['added', 'duplicate', 'failed']);
    expect(component.rows()[1].note).toContain('Already in your library');
    expect(component.summary()).toBe('1 added, 1 already in your library, 1 failed.');
  });

  it('stops after the current book when asked', async () => {
    await setup([file('one.epub'), file('two.epub')]);
    vi.spyOn(books, 'createOrMatch').mockImplementation(() => {
      component.cancel();
      return of({ book: book({ id: '1' }), created: true });
    });
    vi.spyOn(books, 'uploadFile').mockReturnValue(of({ type: HttpEventType.Response } as never));
    await component.start();
    expect(component.rows().map((r) => r.status)).toEqual(['added', 'ready']);
    expect(component.summary()).toBe('1 added, 1 not added.');
  });

  it('does not start with a blank title', async () => {
    await setup([file('one.epub')]);
    component.edit(0, 'title', '  ');
    expect(component.canStart()).toBe(false);
  });
});
