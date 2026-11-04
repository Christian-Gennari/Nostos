import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AddBookModal } from './add-book-modal.component';
import { epubFile } from './testing/epub-file.fixture';

const WALDEN_METADATA =
  '<dc:title>Walden</dc:title><dc:creator>Henry David Thoreau</dc:creator>';

describe('AddBookModal: title and author from a chosen EPUB (#1358)', () => {
  let component: AddBookModal;
  let fixture: ComponentFixture<AddBookModal>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AddBookModal] }).compileComponents();
    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', []);
    await fixture.whenStable();
    component.startIntent('upload');
    fixture.detectChanges();
  });

  function choose(file: File): void {
    component.onFileSelected({ target: { files: [file] } } as unknown as Event);
  }

  async function settled(): Promise<void> {
    await vi.waitFor(async () => {
      await fixture.whenStable();
      expect(component.form.author || component.form.title).toBeTruthy();
    });
    // Let a late fill land, if one is coming, before asserting it did not.
    await new Promise((resolve) => setTimeout(resolve, 200));
    await fixture.whenStable();
  }

  it('replaces the file-name title and fills the empty author', async () => {
    choose(await epubFile('walden-final_v2.epub', WALDEN_METADATA));
    expect(component.form.title).toBe('walden final v2');

    await vi.waitFor(() => expect(component.form.author).toBe('Henry David Thoreau'));
    expect(component.form.title).toBe('Walden');
    expect(component.form.type).toBe('ebook');

    fixture.detectChanges();
    const title = fixture.nativeElement.querySelector('#book-title') as HTMLInputElement;
    await fixture.whenStable();
    expect(title.value).toBe('Walden');
  });

  it('keeps a title and an author the owner typed', async () => {
    component.form.title = 'My Walden';
    component.form.author = 'H. D. Thoreau';
    choose(await epubFile('walden.epub', WALDEN_METADATA));
    await settled();

    expect(component.form.title).toBe('My Walden');
    expect(component.form.author).toBe('H. D. Thoreau');
  });

  it('keeps a title typed over the file-name guess while the EPUB was being read', async () => {
    choose(await epubFile('walden.epub', WALDEN_METADATA));
    component.form.title = 'Typed meanwhile';
    await vi.waitFor(() => expect(component.form.author).toBe('Henry David Thoreau'));

    expect(component.form.title).toBe('Typed meanwhile');
  });

  it('ignores the metadata of a file that was replaced before it was read', async () => {
    const first = await epubFile('walden.epub', WALDEN_METADATA);
    const second = new File(['%PDF'], 'meditations.pdf', { type: 'application/pdf' });
    choose(first);
    choose(second);
    await settled();

    expect(component.form.author).toBeFalsy();
    expect(component.form.title).toBe('walden');
  });

  it('keeps the file-name title when the EPUB cannot be read', async () => {
    choose(new File(['not a zip'], 'the-magic-mountain.epub', { type: 'application/epub+zip' }));
    await settled();

    expect(component.form.title).toBe('the magic mountain');
    expect(component.form.author).toBeFalsy();
  });
});
