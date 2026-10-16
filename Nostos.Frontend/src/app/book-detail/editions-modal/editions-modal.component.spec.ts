import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EditionsModal, LinkCandidate, WorkMember } from './editions-modal.component';
import { LinkableBookDto } from '../../core/dtos/book.dtos';

/**
 * The membership modal is presentation only: it owns no data and calls no
 * service. These tests pin the contract the page depends on — what it renders
 * for a given input, and which intent it emits — plus the two states that are
 * easy to get wrong: a member row must be silent about an action it cannot
 * offer, and a lone book must still get a usable surface.
 */
describe('EditionsModal', () => {
  let fixture: ComponentFixture<EditionsModal>;
  let component: EditionsModal;

  const members: WorkMember[] = [
    { id: 'current', title: 'Nicomachean Ethics', author: 'Aristotle' },
    { id: 'sibling', title: 'The Nicomachean Ethics', author: 'Aristotle' },
  ];

  const candidates: LinkableBookDto[] = [
    { id: 'c1', title: 'Meditations', author: 'Marcus Aurelius', workId: 'w1', editionCount: 1 },
    { id: 'c2', title: 'Justice', author: 'Michael J. Sandel', workId: 'w2', editionCount: 2 },
  ];

  /** The raw input values accepted by `setInput`, not the input signals. */
  type InputValues = {
    isOpen: boolean;
    members: WorkMember[];
    candidates: LinkCandidate[];
    currentBookId: string | null;
    bookTitle: string;
    busy: boolean;
    searching: boolean;
    query: string;
  };

  async function setup(overrides: Partial<InputValues> = {}): Promise<void> {
    await TestBed.configureTestingModule({ imports: [EditionsModal] }).compileComponents();
    fixture = TestBed.createComponent(EditionsModal);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('members', members);
    fixture.componentRef.setInput('candidates', candidates);
    fixture.componentRef.setInput('currentBookId', 'current');
    fixture.componentRef.setInput('bookTitle', 'Nicomachean Ethics');

    for (const [key, value] of Object.entries(overrides)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
  }

  it('renders nothing while closed', async () => {
    await setup({ isOpen: false });
    expect(fixture.nativeElement.querySelector('.editions-modal-card')).toBeNull();
  });

  it('marks the current book with the page\'s selection state and offers Unlink only for the others', async () => {
    await setup();

    const rows = fixture.nativeElement.querySelectorAll('.manage-member');
    expect(rows.length).toBe(2);

    // The book you are on is context: no action, and it says so in words — in the
    // page's own "you are here" treatment (selection fill + Current badge), not a
    // recipe of its own.
    expect(rows[0].classList.contains('is-current')).toBe(true);
    expect(rows[0].querySelector('.manage-member-flag')?.textContent).toContain('Current');
    expect(rows[0].querySelector('.manage-member-action')).toBeNull();

    expect(rows[1].classList.contains('is-current')).toBe(false);
    expect(rows[1].querySelector('.manage-member-flag')).toBeNull();
    expect(rows[1].querySelector('.manage-member-action')?.textContent?.trim()).toBe('Unlink');
  });

  it('emits the member to unlink rather than acting itself', async () => {
    await setup();

    let emitted: WorkMember | undefined;
    component.unlinkRequest.subscribe((m) => (emitted = m));

    (fixture.nativeElement.querySelector('.manage-member-action') as HTMLButtonElement).click();

    expect(emitted?.id).toBe('sibling');
    expect(emitted?.title).toBe('The Nicomachean Ethics');
  });

  it('emits the candidate to link, and reports its group size', async () => {
    await setup();

    let emitted: LinkableBookDto | undefined;
    component.linkRequest.subscribe((c) => (emitted = c));

    const rows = fixture.nativeElement.querySelectorAll('.manage-link-candidate');
    expect(rows.length).toBe(2);
    // A work with two editions is one choice, and says how big the merge is.
    expect(rows[1].textContent).toContain('2 editions');

    (rows[0] as HTMLButtonElement).click();
    expect(emitted?.id).toBe('c1');
  });

  it('labels the candidate row with an explicit Link action', async () => {
    await setup();

    // The row is the click target, but a click target with no label reads as
    // static text — the only other cue would be hover, which touch never shows.
    const action = fixture.nativeElement.querySelector('.manage-link-candidate-action');
    expect(action).toBeTruthy();
    expect(action.textContent).toContain('Link');

    // A label inside the row button, not a nested <button>: that is invalid HTML
    // and would swallow the click target.
    expect(action.tagName.toLowerCase()).toBe('span');
  });

  it('reports an empty list differently once a query is typed', async () => {
    await setup({ candidates: [] });

    expect(fixture.nativeElement.querySelector('.manage-link-empty')?.textContent)
      .toContain('No other books');

    fixture.componentRef.setInput('query', 'medit');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.manage-link-empty')?.textContent)
      .toContain('No matching books');
  });

  it('still renders a usable surface for a book alone in its work', async () => {
    // The case the whole surface exists for: one member, nothing to unlink.
    await setup({
      members: [{ id: 'current', title: 'Solo Book', author: null }],
      currentBookId: 'current',
    });

    expect(fixture.nativeElement.querySelectorAll('.manage-member').length).toBe(1);
    expect(fixture.nativeElement.querySelector('.manage-member-action')).toBeNull();
    expect(fixture.nativeElement.querySelector('.manage-link-search')).toBeTruthy();
    // No author must not render as a blank line.
    expect(fixture.nativeElement.querySelector('.manage-member-author')?.textContent)
      .toContain('Unknown author');
  });

  it('emits query changes instead of holding the search text', async () => {
    await setup();

    const seen: string[] = [];
    component.queryChange.subscribe((q) => seen.push(q));

    const input = fixture.nativeElement.querySelector('.manage-link-search') as HTMLInputElement;
    input.value = 'medit';
    input.dispatchEvent(new Event('input'));

    expect(seen).toEqual(['medit']);
  });

  it('uses the canonical input and button, while the candidate row stays product-owned', async () => {
    await setup();

    const search = fixture.nativeElement.querySelector('.manage-link-search') as HTMLInputElement;
    expect(search.classList.contains('nostos-form-control--input')).toBe(true);
    expect(search.classList.contains('nostos-form-control--compact')).toBe(true);

    const unlink = fixture.nativeElement.querySelector('.manage-member-action') as HTMLButtonElement;
    const candidate = fixture.nativeElement.querySelector('.manage-link-candidate') as HTMLButtonElement;
    // Unlink is appButton sm; without the component imported it rendered as a
    // bare browser button. The candidate row is itself the button, so it is not.
    expect(unlink.classList.contains('nostos-button')).toBe(true);
    expect(candidate.classList.contains('nostos-button')).toBe(false);
  });

  it('names each member\'s format, so editions sharing a title can be told apart', async () => {
    await setup({
      members: [
        { id: 'current', title: 'Anna Karenina', author: 'Leo Tolstoy', format: 'EPUB', formatIcon: 'ebook' },
        { id: 'sibling', title: 'Anna Karenina', author: 'Leo Tolstoy', format: 'Audiobook', formatIcon: 'audiobook' },
      ],
    });

    const formats = Array.from<HTMLElement>(
      fixture.nativeElement.querySelectorAll('.manage-member-format'),
    ).map((el) => el.textContent?.trim());
    expect(formats).toEqual(['EPUB', 'Audiobook']);
  });

  it('names a single-edition candidate\'s format, and leaves a multi-edition work its count', async () => {
    await setup({
      candidates: [
        { id: 'c1', title: 'Meditations', author: 'Marcus Aurelius', editionCount: 1, format: 'Audiobook', formatIcon: 'audiobook' },
        { id: 'c2', title: 'Justice', author: 'Michael J. Sandel', editionCount: 2 },
      ],
    });

    const rows = Array.from<HTMLElement>(fixture.nativeElement.querySelectorAll('.manage-link-candidate'));
    expect(rows[0].querySelector('.manage-member-format')?.textContent?.trim()).toBe('Audiobook');
    expect(rows[1].querySelector('.manage-member-format')).toBeNull();
    expect(rows[1].querySelector('.manage-link-group-flag')?.textContent?.trim()).toBe('2 editions');
  });

  it('will not close while a mutation is in flight', async () => {
    await setup({ busy: true });

    let closed = false;
    component.close.subscribe(() => (closed = true));

    const close = fixture.nativeElement.querySelector('.editions-modal-close') as HTMLButtonElement;
    // The shared icon button, not a hand-rolled copy of one: that migration is
    // what this pins.
    expect(close.classList.contains('icon-btn')).toBe(true);
    close.click();
    (fixture.nativeElement.querySelector('.modal-backdrop') as HTMLElement).click();

    expect(closed).toBe(false);
    expect(close.disabled).toBe(true);
  });

  it('rests on a handful of candidates and says how to see the rest', async () => {
    // The caller's first page is 20 newest books: showing all of them made the
    // opening state a scroll inside a 640px dialog.
    const many: LinkableBookDto[] = Array.from({ length: 7 }, (_, i) => ({
      id: `c${i}`,
      title: `Book ${i}`,
      author: null,
      workId: `w${i}`,
      editionCount: 1,
    }));
    await setup({ candidates: many });

    expect(fixture.nativeElement.querySelectorAll('.manage-link-candidate').length).toBe(5);
    expect(fixture.nativeElement.querySelector('.manage-link-more')?.textContent).toContain(
      'most recent'
    );

    // A query is a deliberate act, so its result set is not truncated — and the
    // note is about the resting state, so it goes away.
    fixture.componentRef.setInput('query', 'book');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.manage-link-candidate').length).toBe(7);
    expect(fixture.nativeElement.querySelector('.manage-link-more')).toBeNull();
  });

  it('names the book in the header so the surface is not ambiguous', async () => {
    await setup();
    expect(fixture.nativeElement.querySelector('.editions-modal-subtitle')?.textContent)
      .toContain('Nicomachean Ethics');
  });
});
