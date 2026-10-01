import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';

import { AddBookModal } from './add-book-modal.component';
import { Book, BooksService } from '../core/services/books.service';
import { ProvidersService } from '../core/services/providers.service';
import {
  ProviderAcquisition,
  ProviderDiscoverySearchResult,
  ProviderItem,
  ProviderSummary,
} from '../core/dtos/provider.dtos';
import { ToastService } from '../core/services/toast.service';

/**
 * The "From a Source" tab (issue #167).
 *
 * The states worth pinning are the ones a user notices when they are wrong:
 * which sources are offered, what a result looks like, that the import cannot
 * look finished before the server says it is, and that a failure explains
 * itself instead of silently doing nothing.
 */
const collections = [{ id: 'root', name: 'Root', parentId: null }];

const gutenberg: ProviderSummary = {
  id: 'gutenberg',
  displayName: 'Project Gutenberg',
  capabilities: ['search', 'itemretrieval', 'ebookacquisition'],
  rightsNotice: 'Public domain in the USA (Project Gutenberg)',
};

const librivox: ProviderSummary = {
  id: 'librivox',
  displayName: 'LibriVox',
  capabilities: ['search', 'itemretrieval', 'audiobookacquisition'],
  rightsNotice: 'Public domain recordings (LibriVox)',
};

const wikisource: ProviderSummary = {
  id: 'wikisource',
  displayName: 'Wikisource',
  capabilities: ['search', 'itemretrieval', 'ebookacquisition', 'coverart', 'rightsinformation'],
  rightsNotice: null,
};

const pride: ProviderItem = {
  providerId: 'gutenberg',
  externalId: '1342',
  mediaKind: 'ebook',
  title: 'Pride and Prejudice',
  subtitle: null,
  author: 'Jane Austen',
  description: null,
  language: 'English',
  publisher: null,
  publishedDate: null,
  categories: 'England -- Fiction',
  narrator: null,
  duration: null,
  pageCount: null,
  assets: [
    {
      id: 'epub3-images',
      kind: 'ebook',
      label: 'EPUB3 (E-readers incl. Send-to-Kindle)',
      sourceFormat: 'application/epub+zip',
      sizeBytes: 24835578,
      isPreferred: true,
    },
    {
      id: 'epub-noimages',
      kind: 'ebook',
      label: 'EPUB (no images, older E-readers)',
      sourceFormat: 'application/epub+zip',
      sizeBytes: 558381,
      isPreferred: false,
    },
  ],
  coverUrl: '/api/providers/gutenberg/items/1342/cover',
  sourceUrl: 'https://www.gutenberg.org/ebooks/1342',
  rightsStatement: 'Public domain in the USA.',
  partCount: null,
};

function job(overrides: Partial<ProviderAcquisition> = {}): ProviderAcquisition {
  return {
    jobId: 'job-1',
    state: 'running',
    stage: 'downloading',
    percent: 42,
    detail: '1/1 files',
    providerId: 'gutenberg',
    externalId: '1342',
    assetId: 'epub3-images',
    bookId: null,
    errorCode: null,
    message: null,
    createdAt: '2026-09-17T00:00:00Z',
    updatedAt: '2026-09-17T00:00:00Z',
    ...overrides,
  };
}

describe('AddBookModal — From a Source', () => {
  let component: AddBookModal;
  let fixture: ComponentFixture<AddBookModal>;
  let providers: ProvidersService;
  let books: BooksService;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AddBookModal],
      providers: [provideRouter([])],
    }).compileComponents();

    providers = TestBed.inject(ProvidersService);
    books = TestBed.inject(BooksService);
    vi.spyOn(providers, 'list').mockReturnValue(of([gutenberg, librivox, wikisource]));
    vi.spyOn(providers, 'searchAll').mockReturnValue(
      of({
        items: [{ ...pride, assets: [] }],
        hasMore: false,
        sources: [
          {
            providerId: 'gutenberg',
            displayName: 'Project Gutenberg',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
          {
            providerId: 'librivox',
            displayName: 'LibriVox',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
          {
            providerId: 'wikisource',
            displayName: 'Wikisource',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
        ],
      }),
    );
    // Search results carry no assets by design, so selecting one fetches the
    // full item. Mocked here so the tests never reach for the network.
    vi.spyOn(providers, 'item').mockImplementation((providerId, externalId) =>
      of({ ...pride, providerId, externalId }),
    );

    fixture = TestBed.createComponent(AddBookModal);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('isOpen', true);
    fixture.componentRef.setInput('collections', collections);
    await fixture.whenStable();
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('uses one shallow metadata disclosure instead of form tabs', () => {
    expect(fixture.nativeElement.querySelector('[role="tablist"]')).toBeNull();

    const disclosure = fixture.nativeElement.querySelector(
      'details.metadata-disclosure',
    ) as HTMLDetailsElement;
    expect(disclosure).toBeTruthy();
    expect(disclosure.open).toBe(false);
    expect(disclosure.querySelectorAll('details')).toHaveLength(0);
    expect(disclosure.textContent).toContain('More details');
    expect(disclosure.textContent).toContain('Publication');
  });

  it('refuses to enter the source search when editing a book', () => {
    fixture.componentRef.setInput('book', { id: 'b1', title: 'Meditations' } as unknown as Book);
    fixture.detectChanges();

    component.enterSourceMode();

    // Importing is a way to ADD a book, so it has no place in edit mode.
    expect(component.sourceMode()).toBe(false);
  });

  it('starts in the source search when the caller asks for it', async () => {
    fixture.componentRef.setInput('sourceFirst', true);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.sourceMode()).toBe(true);
    expect(fixture.nativeElement.querySelector('[role="tablist"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('#source-query')).toBeTruthy();
    // And the sources have to actually be loaded, or the search has nothing to
    // run against. Found by pressing the button, not by reading the code.
    expect(component.providerList().length).toBeGreaterThan(0);
  });

  it('keeps provider discovery as its own first step with a manual fallback', async () => {
    fixture.componentRef.setInput('sourceFirst', true);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.sourceMode()).toBe(true);
    expect(fixture.nativeElement.querySelector('#source-query')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.review-flow')).toBeNull();
    expect(fixture.nativeElement.querySelector('.review-summary')).toBeNull();
    expect(fixture.nativeElement.querySelector('#book-title')).toBeNull();
    expect(fixture.nativeElement.querySelector('.add-mode-button')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Manual entry');
    expect(fixture.nativeElement.textContent).not.toContain('No file or source required.');

    const manual = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.trim() === 'Add manually');
    expect(manual).toBeTruthy();

    manual!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(component.sourceMode()).toBe(false);
    expect(component.flowIntent()).toBe('manual');
    expect(fixture.nativeElement.querySelector('#source-query')).toBeNull();
    expect(fixture.nativeElement.querySelector('.review-flow')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('#book-type')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.file-drop-zone--manual')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('EPUB, PDF');
    expect(fixture.nativeElement.textContent).toContain('Identify by ISBN');
  });

  it('lets manual intake upload a PDF without changing the book type first', async () => {
    fixture.componentRef.setInput('sourceFirst', true);
    await fixture.whenStable();
    fixture.detectChanges();

    const manual = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.trim() === 'Add manually');
    expect(manual).toBeTruthy();
    manual!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    // Manual entry starts as a physical record, but file upload is still visible.
    // Choosing the file decides the digital type; the user should not have to
    // understand that PDF is represented internally as an E-book first.
    expect(component.form.type).toBe('physical');

    const input = fixture.nativeElement.querySelector(
      '.file-drop-zone--manual input[type="file"]',
    ) as HTMLInputElement;
    expect(input).toBeTruthy();
    expect(input.accept).toContain('.pdf');

    const file = new File(['%PDF-1.7'], 'book.pdf', { type: 'application/pdf' });
    Object.defineProperty(input, 'files', { value: [file] });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(component.selectedFile()).toBe(file);
    expect(component.form.type).toBe('ebook');
    expect(component.form.title).toBe('book');
    expect(fixture.nativeElement.textContent).toContain('book.pdf');
    expect(component.bookTypeOptions.find((option) => option.value === 'ebook')?.label).toContain(
      'PDF',
    );
  });

  it('uses canonical controls for the ordinary source search field and import action', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    fixture.detectChanges();

    const query = fixture.nativeElement.querySelector('#source-query') as HTMLInputElement;
    expect(query.classList.contains('nostos-form-control')).toBe(true);

    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    component.selectSourceItem(pride);
    fixture.detectChanges();

    // The next action lives in the persistent footer (#641).
    const action = fixture.nativeElement.querySelector('[data-testid="source-use-book"]') as HTMLButtonElement;
    expect(action.classList.contains('nostos-button')).toBe(true);
    expect(action.classList.contains('nostos-button--primary')).toBe(true);
  });

  it('loads provider metadata without requiring a provider choice', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(providers.list).toHaveBeenCalled();
    expect(component.sourceKind()).toBe('all');
    expect(fixture.nativeElement.textContent).toContain('Search free books and audiobooks');
    expect(fixture.nativeElement.querySelectorAll('.source-choice').length).toBe(0);
  });

  it('offers material filters instead of provider buttons', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    fixture.detectChanges();

    const labels = Array.from(
      fixture.nativeElement.querySelectorAll('.source-filter') as NodeListOf<HTMLButtonElement>,
    ).map((button) => button.textContent?.trim());

    expect(labels).toEqual(['All', 'E-books', 'Audiobooks']);
  });

  it('keeps unified search usable if the provider metadata list fails', async () => {
    vi.spyOn(providers, 'list').mockReturnValue(throwError(() => new Error('down')));

    component.enterSourceMode();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.providersError()).toBeTruthy();
    expect(fixture.nativeElement.querySelector('#source-query')).toBeTruthy();
  });

  it('will not search on a single character, and aggregate-searches on two', async () => {
    component.enterSourceMode();
    await fixture.whenStable();

    component.sourceQuery.set('p');
    component.searchSource();
    expect(providers.searchAll).not.toHaveBeenCalled();

    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();

    expect(providers.searchAll).toHaveBeenCalledWith('pride', undefined);
  });

  it('keeps the query and reruns aggregate search when the material filter changes', async () => {
    component.enterSourceMode();
    await fixture.whenStable();

    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();

    component.setSourceKind('ebook');
    await fixture.whenStable();

    expect(component.sourceQuery()).toBe('pride');
    expect(providers.searchAll).toHaveBeenLastCalledWith('pride', 'ebook');

    component.setSourceKind('audiobook');
    await fixture.whenStable();
    expect(providers.searchAll).toHaveBeenLastCalledWith('pride', 'audiobook');
  });

  it('keeps a successful provider notice attributed to that provider', async () => {
    vi.spyOn(providers, 'searchAll').mockReturnValue(
      of({
        items: [{ ...pride, assets: [] }],
        hasMore: false,
        sources: [
          {
            providerId: 'librivox',
            displayName: 'LibriVox',
            succeeded: true,
            notice: 'Prefix search only.',
            errorCode: null,
          },
        ],
      }),
    );

    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('LibriVox: Prefix search only.');
  });

  it('shows unavailable rather than no matches when every provider fails', async () => {
    vi.spyOn(providers, 'searchAll').mockReturnValue(
      of({
        items: [],
        hasMore: false,
        sources: [
          {
            providerId: 'gutenberg',
            displayName: 'Project Gutenberg',
            succeeded: false,
            notice: null,
            errorCode: 'provider_unavailable',
          },
          {
            providerId: 'wikisource',
            displayName: 'Wikisource',
            succeeded: false,
            notice: null,
            errorCode: 'provider_unavailable',
          },
        ],
      }),
    );

    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('The free book sources are unavailable right now.');
    expect(text).not.toContain('No matching books were found');
  });

  it('keeps successful results visible and attributes a failed source', async () => {
    vi.spyOn(providers, 'searchAll').mockReturnValue(
      of({
        items: [{ ...pride, assets: [] }],
        hasMore: false,
        sources: [
          {
            providerId: 'gutenberg',
            displayName: 'Project Gutenberg',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
          {
            providerId: 'wikisource',
            displayName: 'Wikisource',
            succeeded: false,
            notice: null,
            errorCode: 'provider_unavailable',
          },
        ],
      }),
    );

    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.source-result').length).toBe(1);
    // Partial availability is one compact row, not a stack of errors (#641):
    // each source's outcome as a dot Badge (#658), plus one announced line.
    const partial = fixture.nativeElement.querySelector('[data-testid="source-partial"]') as HTMLElement;
    expect(partial.textContent).toContain('Not searched right now: Wikisource');
    const badges = Array.from(partial.querySelectorAll('.nostos-badge') as NodeListOf<HTMLElement>);
    expect(badges.map((b) => b.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Project Gutenberg',
      'Wikisource · unavailable',
    ]);
    expect(badges[0].classList.contains('nostos-badge--success')).toBe(true);
    expect(badges[1].classList.contains('nostos-badge--danger')).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Pride and Prejudice');
  });

  it('keeps provider-qualified identity when two sources reuse the same external id', async () => {
    const wikiItem = {
      ...pride,
      providerId: 'wikisource',
      assets: [],
      coverUrl: '/api/providers/wikisource/items/1342/cover',
    };
    vi.spyOn(providers, 'searchAll').mockReturnValue(
      of({
        items: [{ ...pride, assets: [] }, wikiItem],
        hasMore: false,
        sources: [
          {
            providerId: 'gutenberg',
            displayName: 'Project Gutenberg',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
          {
            providerId: 'wikisource',
            displayName: 'Wikisource',
            succeeded: true,
            notice: null,
            errorCode: null,
          },
        ],
      }),
    );

    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    fixture.detectChanges();

    const rows = fixture.nativeElement.querySelectorAll('.source-result') as NodeListOf<HTMLButtonElement>;
    expect(rows.length).toBe(2);
    rows[1].click();
    await fixture.whenStable();

    expect(providers.item).toHaveBeenLastCalledWith('wikisource', '1342');
    expect(component.selectedItem()?.providerId).toBe('wikisource');
    expect(component.sourceItemKey(component.sourceResults()[0])).not.toBe(
      component.sourceItemKey(component.sourceResults()[1]),
    );
  });

  it('ignores late detail from a previously selected provider with the same external id', () => {
    const gutenbergDetail = new Subject<ProviderItem>();
    const wikisourceDetail = new Subject<ProviderItem>();
    vi.spyOn(providers, 'item').mockImplementation((providerId) =>
      providerId === 'gutenberg' ? gutenbergDetail : wikisourceDetail,
    );

    const g = { ...pride, assets: [] };
    const w = { ...pride, providerId: 'wikisource', assets: [] };

    component.selectSourceItem(g);
    component.selectSourceItem(w);

    gutenbergDetail.next(pride);
    expect(component.selectedDetail()).toBeNull();

    wikisourceDetail.next({ ...pride, providerId: 'wikisource' });
    expect(component.selectedDetail()?.providerId).toBe('wikisource');
  });

  it('preserves an explicitly selected PDF through metadata review and import', async () => {
    const detail: ProviderItem = {
      ...pride,
      assets: [
        {
          ...pride.assets[0],
          id: 'epub',
          label: 'EPUB',
          sourceFormat: 'application/epub+zip',
          isPreferred: true,
        },
        {
          ...pride.assets[0],
          id: 'pdf',
          label: 'PDF',
          sourceFormat: 'application/pdf',
          isPreferred: false,
        },
      ],
    };
    vi.spyOn(providers, 'item').mockReturnValue(of(detail));
    const acquire = vi.spyOn(providers, 'acquire').mockReturnValue(of(job({ assetId: 'pdf' })));

    component.selectSourceItem({ ...pride, assets: [] });
    await fixture.whenStable();

    expect(component.selectedFormatFamily()).toBe('EPUB');
    component.selectFormatFamily('PDF');
    expect(component.selectedAssetId()).toBe('pdf');

    component.seedFromSelectedItem();
    component.form.title = 'Pride and Prejudice — reviewed';
    component.importSelected();

    expect(component.form.type).toBe('ebook');
    expect(acquire).toHaveBeenCalledWith(
      'gutenberg',
      expect.objectContaining({
        externalId: '1342',
        assetId: 'pdf',
      }),
    );
  });

  it('renders results with their metadata and the proxied cover', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();
    fixture.detectChanges();

    const rows = fixture.nativeElement.querySelectorAll('.source-result');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('Pride and Prejudice');
    expect(rows[0].textContent).toContain('Jane Austen');
    expect(rows[0].textContent).toContain('E-book · Project Gutenberg');

    // The browser never talks to the source directly: the URL is Nostos's own.
    expect(fixture.nativeElement.querySelector('.source-cover').getAttribute('src')).toBe(
      '/api/providers/gutenberg/items/1342/cover',
    );
  });

  it("preselects the source's preferred format, collapsed, and names who claims the rights", async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();

    component.selectSourceItem(pride);
    fixture.detectChanges();

    expect(component.selectedAssetId()).toBe('epub3-images');

    // The formats sit behind a collapsed disclosure. `textContent` reports them
    // whether or not it is open — the same way `[hidden]` reported `true` on a
    // visible element — so this asserts the state, not the presence of a string.
    const formats = fixture.nativeElement.querySelector('.source-formats') as HTMLDetailsElement;
    expect(formats).toBeTruthy();
    expect(formats.open).toBe(false);
    expect(formats.querySelector('.source-formats-current')?.textContent).toContain(
      'EPUB3 (E-readers incl. Send-to-Kindle)',
    );
    const assets = component.selectedSourceItem()?.assets ?? [];
    // More than one is the reason it is a disclosure at all.
    expect(assets.length).toBeGreaterThan(1);
    expect(formats.querySelectorAll('.source-asset').length).toBe(assets.length);

    // Status first, then who said it — in brackets, and the attribution is the
    // link. The source's own trailing full stop is dropped, because the line now
    // ends with the bracket rather than with the statement. The two are separate
    // nodes and the gap between them is a CSS margin (Angular strips whitespace
    // between inline elements), so each part is asserted on its own.
    const status = fixture.nativeElement
      .querySelector('.source-rights-status')
      ?.textContent?.trim();
    expect(status).toBe('Public domain in the USA');

    const link = fixture.nativeElement.querySelector(
      '.source-rights-link',
    ) as HTMLAnchorElement | null;
    expect(link).toBeTruthy();
    expect(link?.textContent).toContain('(via Project Gutenberg');
    // One link, and it is the attribution: a separate "view at source" would
    // have named the same place twice.
    expect(fixture.nativeElement.textContent).not.toContain('View at source');
  });

  it('asks for collections once, only after discovery moves to review', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();

    component.selectSourceItem(pride);
    fixture.detectChanges();

    // Discovery is now a separate step, so review-only controls must not exist
    // in the DOM while the user is still choosing a provider result.
    expect(fixture.nativeElement.querySelectorAll('app-collection-picker').length).toBe(0);

    component.seedFromSelectedItem();
    fixture.detectChanges();

    // Once the chosen source becomes the prefilled review, collections are
    // asked exactly once there.
    expect(fixture.nativeElement.querySelectorAll('app-collection-picker').length).toBe(1);
  });

  it('shows the source cover as already handled and offers no upload on the import path', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    component.sourceQuery.set('pride');
    component.searchSource();
    await fixture.whenStable();

    component.selectSourceItem(pride);
    fixture.detectChanges();

    // The import brings both files itself. A dropped file on this path is
    // discarded without a word, so neither upload belongs here at all.
    const inputs = [
      ...fixture.nativeElement.querySelectorAll('input[type="file"]'),
    ] as HTMLInputElement[];
    expect(inputs.some((i) => (i.getAttribute('accept') ?? '').includes('.epub'))).toBe(false);
    expect(inputs.some((i) => i.getAttribute('accept') === 'image/*')).toBe(false);

    // Selecting the result still belongs to discovery. The compact review is
    // shown only after the user chooses this book.
    component.seedFromSelectedItem();
    fixture.detectChanges();

    const preview = fixture.nativeElement.querySelector('.review-cover') as HTMLImageElement;
    expect(preview).toBeTruthy();
    expect(preview.getAttribute('src')).toBe(pride.coverUrl);

    const summary = fixture.nativeElement.querySelector('.review-summary') as HTMLElement;
    expect(summary.textContent).toContain('Project Gutenberg');
    expect(summary.textContent).toContain('EPUB');
  });

  it('keeps local file acquisition visible and offers cover upload only after the file is chosen', () => {
    component.startIntent('upload');
    fixture.detectChanges();

    const firstInput = fixture.nativeElement.querySelector(
      '.file-drop-zone--primary input[type="file"]',
    ) as HTMLInputElement;
    expect(firstInput).toBeTruthy();

    const file = new File(['x'], 'book.epub', { type: 'application/epub+zip' });
    Object.defineProperty(firstInput, 'files', { value: [file] });
    firstInput.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const inputs = [
      ...fixture.nativeElement.querySelectorAll('input[type="file"]'),
    ] as HTMLInputElement[];
    expect(inputs.some((i) => (i.getAttribute('accept') ?? '').includes('.epub'))).toBe(true);
    expect(inputs.some((i) => i.getAttribute('accept') === 'image/*')).toBe(true);
    expect(fixture.nativeElement.querySelector('.review-summary')?.textContent).toContain('book.epub');
  });

  it('hides the form submit button on the source tab', async () => {
    component.enterSourceMode();
    await fixture.whenStable();
    fixture.detectChanges();

    const submit = Array.from<Element>(fixture.nativeElement.querySelectorAll('button')).find(
      (b) => b.getAttribute('type') === 'submit',
    );
    expect(submit).toBeUndefined();
  });

  it('closes modal, refreshes library, and toasts when import is accepted', async () => {
    vi.spyOn(providers, 'acquire').mockReturnValue(of(job()));
    const added = vi.fn();
    const closed = vi.fn();
    const toast = TestBed.inject(ToastService);
    const toastSpy = vi.spyOn(toast, 'success');
    component.bookAdded.subscribe(added);
    component.closeModal.subscribe(closed);

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);

    component.importSelected();

    expect(toastSpy).toHaveBeenCalledWith(
      'Import started — it will appear in your library when it finishes.',
    );
    expect(added).toHaveBeenCalled();
    expect(closed).toHaveBeenCalled();
  });

  it('shows error when import fails to start', async () => {
    vi.spyOn(providers, 'acquire').mockReturnValue(
      throwError(() => ({ error: { detail: 'The import could not be started.' } })),
    );
    const closed = vi.fn();
    component.closeModal.subscribe(closed);

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);

    component.importSelected();
    fixture.detectChanges();

    const error = fixture.nativeElement.querySelector('.source-error');
    expect(error.textContent).toContain('The import could not be started.');
    expect(closed).not.toHaveBeenCalled();
  });

  it('sends only the provider, item and asset — never a URL', async () => {
    const acquire = vi.spyOn(providers, 'acquire').mockReturnValue(of(job()));

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);
    component.seedFromSelectedItem();
    component.form.collectionIds = ['root'];
    component.importSelected();

    const body = acquire.mock.calls[0][1];

    expect(acquire).toHaveBeenCalledWith(
      'gutenberg',
      expect.objectContaining({
        externalId: '1342',
        assetId: 'epub3-images',
        collectionIds: ['root'],
      }),
    );

    // Nothing on the wire may carry a location: the server resolves those, and a
    // client-supplied URL would turn this endpoint into a general-purpose fetcher.
    expect(JSON.stringify(body)).not.toMatch(/https?:\/\//);

    // Nothing was edited, so nothing is pinned. The source still owns every
    // field, and a later correction at the source can still reach the library.
    const overrides = Object.values(body.metadataOverrides ?? {});
    expect(overrides.every((value) => value === null)).toBe(true);
  });

  // --- Importing from a prefilled form ----------------------------------

  it('fills the form from the chosen item so it can be finished before importing', async () => {
    const acquire = vi.spyOn(providers, 'acquire');

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);

    component.seedFromSelectedItem();

    expect(component.form.title).toBe('Pride and Prejudice');
    expect(component.form.author).toBe('Jane Austen');
    expect(component.form.language).toBe('English');
    expect(component.form.categories).toBe('England -- Fiction');

    // Picking a result ends at the form, not at a download: the metadata is
    // reviewed and completed first, so the book is created once and already right.
    expect(component.sourceMode()).toBe(false);
    expect(component.seededFromSource()).toBe(true);
    expect(fixture.nativeElement.querySelector('[role="tablist"]')).toBeNull();
    expect(acquire).not.toHaveBeenCalled();
  });

  it('takes the format from the asset being fetched', async () => {
    // The detail endpoint is what says this item is a recording.
    const audiobook = {
      ...pride,
      assets: [{ ...pride.assets[0], id: 'sections', kind: 'audiobook' }],
    } as ProviderItem;
    vi.spyOn(providers, 'item').mockReturnValue(of(audiobook));

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(audiobook);
    await fixture.whenStable();

    // An audiobook asset must not be filed where the ebook reader will open it.
    component.seedFromSelectedItem();

    expect(component.form.type).toBe('audiobook');
  });

  it('imports on save instead of creating from the form', async () => {
    const acquire = vi.spyOn(providers, 'acquire').mockReturnValue(of(job()));
    const create = vi.spyOn(books, 'create');

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);
    component.seedFromSelectedItem();

    component.submit();

    expect(acquire).toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
  });

  it('sends only the fields the user actually edited', async () => {
    const acquire = vi.spyOn(providers, 'acquire').mockReturnValue(of(job()));

    component.enterSourceMode();
    await fixture.whenStable();
    component.selectSourceItem(pride);
    component.seedFromSelectedItem();

    component.form.author = 'Austen, Jane';
    component.form.description = 'My own note.';
    component.importSelected();

    const overrides = acquire.mock.calls[0][1].metadataOverrides;

    expect(overrides?.author).toBe('Austen, Jane');
    expect(overrides?.description).toBe('My own note.');
    // Untouched fields stay null so the source keeps owning them.
    expect(overrides?.title).toBeNull();
    expect(overrides?.categories).toBeNull();
    expect(overrides?.publisher).toBeNull();
  });

  // ------------------------------------------------------------------
  // Responsive, stable, action-stable discovery (#641)
  // ------------------------------------------------------------------

  describe('responsive discovery (#641)', () => {
    const ok = (items: ProviderItem[]): ProviderDiscoverySearchResult => ({
      items,
      hasMore: false,
      sources: [
        { providerId: 'gutenberg', displayName: 'Project Gutenberg', succeeded: true, notice: null, errorCode: null },
      ],
    });
    const emma: ProviderItem = { ...pride, externalId: '158', title: 'Emma', assets: [] };
    const persuasion: ProviderItem = { ...pride, externalId: '105', title: 'Persuasion', assets: [] };

    function el(testId: string): HTMLElement | null {
      return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
    }

    function titles(): string[] {
      return Array.from(fixture.nativeElement.querySelectorAll('.source-result-title') as NodeListOf<HTMLElement>)
        .map((e) => e.textContent?.trim() ?? '');
    }

    async function search(query = 'austen'): Promise<void> {
      component.enterSourceMode();
      await fixture.whenStable();
      component.sourceQuery.set(query);
      component.searchSource();
      fixture.detectChanges();
    }

    it('aborts the previous request when a replacement search is submitted', async () => {
      const first = new Subject<ProviderDiscoverySearchResult>();
      const second = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll')
        .mockReturnValueOnce(first.asObservable())
        .mockReturnValueOnce(second.asObservable());

      await search('austen');
      expect(first.observed).toBe(true);

      component.sourceQuery.set('bronte');
      component.searchSource();
      expect(first.observed).toBe(false);
      expect(second.observed).toBe(true);

      second.next(ok([emma]));
      second.complete();
      fixture.detectChanges();
      expect(titles()).toEqual(['Emma']);
    });

    it('aborts an in-flight search when the material type changes', async () => {
      const pending = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
      await search('a');
      component.sourceQuery.set('austen');
      component.searchSource();
      expect(pending.observed).toBe(true);

      component.sourceQuery.set('x'); // too short to start a replacement
      component.setSourceKind('audiobook');
      expect(pending.observed).toBe(false);
      expect(component.sourceSearching()).toBe(false);
    });

    it('aborts source requests when the modal closes', async () => {
      const pending = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
      await search();
      expect(pending.observed).toBe(true);

      fixture.componentRef.setInput('isOpen', false);
      fixture.detectChanges();
      await fixture.whenStable();
      expect(pending.observed).toBe(false);
    });

    it('reserves the result area while searching and swaps in one ordered set', async () => {
      const pending = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
      await search();

      expect(el('source-results-pane')).not.toBeNull();
      expect(el('source-loading')?.textContent).toContain('Searching free catalogues…');
      expect(titles()).toEqual([]);

      pending.next(ok([pride, emma, persuasion]));
      pending.complete();
      fixture.detectChanges();

      expect(el('source-loading')).toBeNull();
      expect(titles()).toEqual(['Pride and Prejudice', 'Emma', 'Persuasion']);
    });

    it('never reorders or removes sibling results when one is selected', async () => {
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([pride, emma, persuasion])));
      vi.spyOn(providers, 'item').mockImplementation((_, externalId) =>
        of([pride, emma, persuasion].find((item) => item.externalId === externalId)!),
      );
      await search();
      fixture.detectChanges();
      const before = titles();

      component.selectSourceItem(emma);
      fixture.detectChanges();

      expect(titles()).toEqual(before);
      expect(el('source-detail-pane')?.textContent).toContain('Emma');
    });

    it('drives the footer action: Select a book → Loading edition… → Use this book', async () => {
      const detail = new Subject<ProviderItem>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([{ ...pride, assets: [] }])));
      vi.spyOn(providers, 'item').mockReturnValue(detail.asObservable());
      await search();
      fixture.detectChanges();
      const action = () => el('source-use-book') as HTMLButtonElement;

      expect(action().textContent?.trim()).toBe('Select a book');
      expect(action().disabled).toBe(true);

      component.selectSourceItem({ ...pride, assets: [] });
      fixture.detectChanges();
      expect(action().textContent?.trim()).toBe('Loading edition…');
      expect(action().disabled).toBe(true);

      detail.next(pride);
      detail.complete();
      fixture.detectChanges();
      expect(action().textContent?.trim()).toBe('Use this book');
      expect(action().disabled).toBe(false);
      // The in-detail action is gone: the footer owns the next step.
      expect(fixture.nativeElement.querySelector('.source-action')).toBeNull();

      action().click();
      fixture.detectChanges();
      expect(component.sourceMode()).toBe(false);
      expect(component.seededFromSource()).toBe(true);
    });

    it('keeps the footer action unavailable when the edition cannot be loaded', async () => {
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([{ ...pride, assets: [] }])));
      vi.spyOn(providers, 'item').mockReturnValue(throwError(() => new Error('down')));
      await search();
      component.selectSourceItem({ ...pride, assets: [] });
      fixture.detectChanges();

      const action = el('source-use-book') as HTMLButtonElement;
      expect(action.disabled).toBe(true);
      expect(component.sourceImportError()).toContain('could not be loaded');
    });

    it('aborts the previous detail request when another result is chosen', async () => {
      const first = new Subject<ProviderItem>();
      const second = new Subject<ProviderItem>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([pride, emma])));
      vi.spyOn(providers, 'item')
        .mockReturnValueOnce(first.asObservable())
        .mockReturnValueOnce(second.asObservable());
      await search();

      component.selectSourceItem(pride);
      component.selectSourceItem(emma);
      expect(first.observed).toBe(false);
      expect(second.observed).toBe(true);
    });

    it('narrow flow: selecting opens the detail view and Back returns to the same results', async () => {
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([pride, emma, persuasion])));
      await search('austen');
      component.setSourceKind('ebook');
      component.sourceQuery.set('austen');
      component.searchSource();
      fixture.detectChanges();
      const workspace = () => fixture.nativeElement.querySelector('.source-workspace') as HTMLElement;
      expect(workspace().classList.contains('showing-detail')).toBe(false);

      component.selectSourceItem(emma);
      fixture.detectChanges();
      expect(workspace().classList.contains('showing-detail')).toBe(true);
      expect(el('source-back')).not.toBeNull();

      (el('source-back') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(workspace().classList.contains('showing-detail')).toBe(false);
      expect(component.sourceQuery()).toBe('austen');
      expect(component.sourceKind()).toBe('ebook');
      expect(titles()).toEqual(['Pride and Prejudice', 'Emma', 'Persuasion']);
      expect(component.isSelectedSourceItem(emma)).toBe(true);
    });

    // --------------------------------------------------------------------
    // Waiting state and empty detail pane (#658)
    // --------------------------------------------------------------------

    function badgeLabels(testId: string): string[] {
      const host = el(testId);
      if (!host) return [];
      return Array.from(host.querySelectorAll('.nostos-badge') as NodeListOf<HTMLElement>).map(
        (b) => b.textContent?.replace(/\s+/g, ' ').trim() ?? '',
      );
    }

    it('names the catalogues being searched while waiting, for the current material type', async () => {
      const pending = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
      await search();

      expect(badgeLabels('source-searching')).toEqual(['Project Gutenberg', 'LibriVox', 'Wikisource']);

      component.setSourceKind('audiobook');
      fixture.detectChanges();
      expect(badgeLabels('source-searching')).toEqual(['LibriVox']);

      pending.complete();
    });

    it('says a catalogue is slow only after the wait threshold, and clears it on answer', async () => {
      vi.useFakeTimers();
      try {
        const pending = new Subject<ProviderDiscoverySearchResult>();
        vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
        component.enterSourceMode();
        component.sourceQuery.set('austen');
        component.searchSource();
        fixture.detectChanges();
        expect(el('source-slow')).toBeNull();

        vi.advanceTimersByTime(AddBookModal.SOURCE_SLOW_AFTER_MS - 1);
        fixture.detectChanges();
        expect(el('source-slow')).toBeNull();

        vi.advanceTimersByTime(1);
        fixture.detectChanges();
        expect(el('source-slow')?.textContent).toContain('slow to answer');

        pending.next(ok([pride]));
        pending.complete();
        fixture.detectChanges();
        expect(component.sourceSearchSlow()).toBe(false);
        expect(el('source-slow')).toBeNull();
      } finally {
        vi.useRealTimers();
      }
    });

    it('a fast answer never shows the slow notice', async () => {
      vi.useFakeTimers();
      try {
        vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([pride])));
        component.enterSourceMode();
        component.sourceQuery.set('austen');
        component.searchSource();
        vi.advanceTimersByTime(AddBookModal.SOURCE_SLOW_AFTER_MS * 2);
        fixture.detectChanges();
        expect(component.sourceSearchSlow()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('a replaced search does not inherit the previous search\'s slow notice', async () => {
      vi.useFakeTimers();
      try {
        vi.spyOn(providers, 'searchAll').mockReturnValue(new Subject<ProviderDiscoverySearchResult>());
        component.enterSourceMode();
        component.sourceQuery.set('austen');
        component.searchSource();
        vi.advanceTimersByTime(AddBookModal.SOURCE_SLOW_AFTER_MS);
        expect(component.sourceSearchSlow()).toBe(true);

        component.sourceQuery.set('bronte');
        component.searchSource();
        expect(component.sourceSearchSlow()).toBe(false);
      } finally {
        vi.useRealTimers();
      }
    });

    it('keeps an intentional placeholder in the detail pane while searching and before a pick', async () => {
      const pending = new Subject<ProviderDiscoverySearchResult>();
      vi.spyOn(providers, 'searchAll').mockReturnValue(pending.asObservable());
      vi.spyOn(providers, 'item').mockReturnValue(of(emma));
      await search();

      const placeholder = () => el('source-detail-placeholder');
      expect(placeholder()?.textContent).toContain('The book you choose will appear here.');

      pending.next(ok([pride, emma]));
      pending.complete();
      fixture.detectChanges();
      expect(placeholder()?.textContent).toContain('Select a result to see its format and rights.');

      component.selectSourceItem(emma);
      fixture.detectChanges();
      expect(placeholder()).toBeNull();
    });

    it('labels a timed-out source as timed out', async () => {
      vi.spyOn(providers, 'searchAll').mockReturnValue(
        of({
          items: [pride],
          hasMore: false,
          sources: [
            { providerId: 'gutenberg', displayName: 'Project Gutenberg', succeeded: true, notice: null, errorCode: null },
            { providerId: 'librivox', displayName: 'LibriVox', succeeded: false, notice: null, errorCode: 'provider_timeout' },
          ],
        }),
      );
      await search();
      fixture.detectChanges();

      expect(badgeLabels('source-partial')).toEqual(['Project Gutenberg', 'LibriVox · timed out']);
    });

    it('a clean search shows no per-source row', async () => {
      vi.spyOn(providers, 'searchAll').mockReturnValue(of(ok([pride])));
      await search();
      fixture.detectChanges();

      expect(el('source-partial')).toBeNull();
    });
  });
});
