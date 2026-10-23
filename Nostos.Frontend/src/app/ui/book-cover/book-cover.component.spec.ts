import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BookCoverComponent, bookCoverFormat } from './book-cover.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';

describe('BookCoverComponent', () => {
  let fixture: ComponentFixture<BookCoverComponent>;
  let host: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [BookCoverComponent] }).compileComponents();
    fixture = TestBed.createComponent(BookCoverComponent);
    host = fixture.nativeElement as HTMLElement;
  });

  it('shows the cover lazily with alt text from the title', () => {
    fixture.componentRef.setInput('src', '/api/books/b1/cover');
    fixture.componentRef.setInput('bookTitle', 'Dune');
    fixture.detectChanges();

    const img = host.querySelector('img') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/api/books/b1/cover');
    expect(img.getAttribute('alt')).toBe('Dune cover');
    expect(img.getAttribute('loading')).toBe('lazy');
    expect(host.classList.contains('is-placeholder')).toBe(false);
  });

  it('shows one quiet placeholder with the format glyph when there is no cover', () => {
    fixture.componentRef.setInput('bookTitle', 'Dune');
    fixture.componentRef.setInput('format', 'audiobook');
    fixture.detectChanges();

    expect(host.querySelector('img')).toBeNull();
    expect(host.classList.contains('is-placeholder')).toBe(true);
    const placeholder = host.querySelector('.nostos-book-cover__placeholder') as HTMLElement;
    expect(placeholder.getAttribute('role')).toBe('img');
    expect(placeholder.getAttribute('aria-label')).toBe('Dune cover');
    const icon = fixture.debugElement.query(By.directive(NostosIconComponent)).componentInstance as NostosIconComponent;
    expect(icon.name()).toBe('headphones');
  });

  it('falls back to the placeholder when the image fails, and retries a new URL', () => {
    fixture.componentRef.setInput('src', '/broken.jpg');
    fixture.detectChanges();

    host.querySelector('img')!.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('.nostos-book-cover__placeholder')).toBeTruthy();

    fixture.componentRef.setInput('src', '/fixed.jpg');
    fixture.detectChanges();
    expect(host.querySelector('img')?.getAttribute('src')).toBe('/fixed.jpg');
  });

  it('announces nothing when decorative', () => {
    fixture.componentRef.setInput('decorative', true);
    fixture.detectChanges();
    const placeholder = host.querySelector('.nostos-book-cover__placeholder') as HTMLElement;
    expect(placeholder.getAttribute('aria-hidden')).toBe('true');
    expect(placeholder.getAttribute('role')).toBeNull();

    fixture.componentRef.setInput('src', '/c.jpg');
    fixture.detectChanges();
    expect(host.querySelector('img')!.getAttribute('alt')).toBe('');
  });

  it('carries its size rung on the host', () => {
    fixture.componentRef.setInput('size', 'lg');
    fixture.detectChanges();
    expect(host.classList.contains('nostos-book-cover--lg')).toBe(true);
  });
});

describe('bookCoverFormat', () => {
  it('reads the format the way the Library badges do', () => {
    expect(bookCoverFormat({ type: 'audiobook' })).toBe('audiobook');
    expect(bookCoverFormat({ type: 'ebook', fileName: 'x.pdf' })).toBe('pdf');
    expect(bookCoverFormat({ type: 'physical' })).toBe('physical');
    expect(bookCoverFormat({ type: 'ebook', fileName: 'x.epub' })).toBe('ebook');
    expect(bookCoverFormat({})).toBeNull();
    expect(bookCoverFormat(null)).toBeNull();
  });
});
