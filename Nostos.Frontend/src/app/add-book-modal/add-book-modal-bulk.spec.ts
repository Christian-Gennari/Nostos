import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { BooksService } from '../core/services/books.service';

describe('AddBookModal: several ebooks at once', () => {
  let fixture: ComponentFixture<AddBookModal>;
  let component: AddBookModal;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AddBookModal] }).compileComponents();
    vi.spyOn(TestBed.inject(BooksService), 'readFileMetadata').mockReturnValue(
      of({ title: 'Read Title', author: null, language: null, pageCount: null }),
    );
    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', []);
    await fixture.whenStable();
    component.startIntent('manual');
    fixture.detectChanges();
  });

  function pick(files: File[]) {
    const input = fixture.nativeElement.querySelector('[data-testid="bulk-file-input"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: files, configurable: true });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('offers a multiple EPUB/PDF picker that leaves the single-file input alone', () => {
    const input = fixture.nativeElement.querySelector('[data-testid="bulk-file-input"]') as HTMLInputElement;
    expect(input.multiple).toBe(true);
    expect(input.accept).toBe('.epub,.pdf');
  });

  it('shows the list for several files and ignores other formats', () => {
    pick([new File(['a'], 'a.epub'), new File(['b'], 'b.pdf'), new File(['c'], 'c.txt')]);
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="bulk-list"] li');
    expect(rows.length).toBe(2);
    expect(fixture.nativeElement.querySelector('[data-testid="bulk-add"]').textContent).toContain('Add 2 books');
  });

  it('closing the list without adding returns nothing and closes the modal', () => {
    const closed = vi.fn();
    component.closeModal.subscribe(closed);
    pick([new File(['a'], 'a.epub')]);
    component.onBulkClosed({ changed: false });
    expect(component.bulkFiles()).toEqual([]);
    expect(closed).toHaveBeenCalled();
  });
});
