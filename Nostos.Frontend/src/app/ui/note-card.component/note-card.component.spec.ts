import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { NoteCardComponent } from './note-card.component';
import type { Note } from '../../core/dtos/note.dtos';

describe('NoteCard source navigation', () => {
  let fixture: ComponentFixture<NoteCardComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NoteCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(NoteCardComponent);
    fixture.componentInstance.note = {
      id: 'note-1',
      bookId: 'book-1',
      content: 'A saved thought.',
      createdAt: '2026-10-01T00:00:00Z',
      bookTitle: 'A Source Book',
      cfiRange: 'epubcfi(/6/2)',
    } satisfies Note;
    fixture.componentInstance.showSource = true;
  });

  it('keeps the default Library/CFI destination for existing consumers', () => {
    fixture.detectChanges();
    const link = fixture.nativeElement.querySelector('.source-badge') as HTMLAnchorElement;
    const destination = new URL(link.href);

    expect(destination.pathname).toBe('/library/book-1');
    expect(destination.searchParams.get('cfi')).toBe('epubcfi(/6/2)');
    expect(link.textContent).toContain('Return to passage');
  });

  it('uses a context-resolved verified PDF destination when supplied', () => {
    fixture.componentInstance.sourceNavigation = {
      commands: ['/read', 'book-1'],
      queryParams: { sourcePage: 53 },
      label: 'Return to passage',
    };
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('.source-badge') as HTMLAnchorElement;
    const destination = new URL(link.href);

    expect(destination.pathname).toBe('/read/book-1');
    expect(destination.searchParams.get('sourcePage')).toBe('53');
    expect(link.getAttribute('aria-label')).toBe('Return to passage in A Source Book');
  });
});
