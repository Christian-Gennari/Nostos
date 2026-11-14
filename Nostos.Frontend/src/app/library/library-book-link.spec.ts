import { TestBed } from '@angular/core/testing';

import { libraryBookLink } from './library-book-link';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import type { Book } from '../core/dtos/book.dtos';

describe('libraryBookLink', () => {
  let preferences: LibraryPreferencesService;
  const book = { id: 'b1', workId: 'w1' } as unknown as Book;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    preferences = TestBed.inject(LibraryPreferencesService);
  });

  it('links to the edition the reader last had open for the work', () => {
    preferences.setActiveEditionId('w1', 'b2');

    expect(libraryBookLink(book, false, preferences)).toEqual(['/library', 'b2']);
  });

  it('has no link at all while Select mode owns the press', () => {
    expect(libraryBookLink(book, true, preferences)).toBeNull();
  });
});
