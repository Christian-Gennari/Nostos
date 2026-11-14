import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryEmptyState } from './library-empty-state.component';

describe('LibraryEmptyState', () => {
  let fixture: ComponentFixture<LibraryEmptyState>;
  const el = () => fixture.nativeElement as HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [LibraryEmptyState] }).compileComponents();
    fixture = TestBed.createComponent(LibraryEmptyState);
    fixture.detectChanges();
  });

  it('offers Add your first book when the library itself is empty', () => {
    const button = el().querySelector('button') as HTMLButtonElement;
    expect(el().textContent).toContain('Your library is empty');
    expect(button.textContent?.trim()).toBe('Add your first book');
  });

  it('names the search that found nothing and offers to clear just it', () => {
    fixture.componentRef.setInput('search', '  zzz  ');
    fixture.detectChanges();

    expect(el().textContent).toContain('No books match "zzz"');
    expect(el().querySelector('button')?.textContent?.trim()).toBe('Clear search');
  });

  it('offers to clear the filters when those emptied the shelf', () => {
    fixture.componentRef.setInput('hasFilters', true);
    fixture.detectChanges();

    expect(el().textContent).toContain('Nothing matches these filters');
    expect(el().querySelector('button')?.textContent?.trim()).toBe('Clear filters');
  });

  it('reports the action that was pressed', () => {
    const cleared: boolean[] = [];
    fixture.componentInstance.clearFilters.subscribe(() => cleared.push(true));
    fixture.componentRef.setInput('hasFilters', true);
    fixture.detectChanges();

    (el().querySelector('button') as HTMLButtonElement).click();

    expect(cleared).toEqual([true]);
  });

  it('keeps the shelf class the layout and e2e hooks expect', () => {
    expect(el().classList.contains('books-empty-state')).toBe(true);
  });
});
