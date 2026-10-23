import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ToastService } from '../../core/services/toast.service';
import { DEFAULT_STUDIO_PREFERENCES, StudioPreferencesService } from '../page-settings/studio-preferences.service';
import { signal } from '@angular/core';
import { ExportDialogComponent } from './export-dialog.component';
import { EXPORT_FORMATS, type ExportFormat } from './export-formats';
import type { ExportDocument, PageSetup } from './export-model';
import type { ExportTreeItem } from './export-order';
import { type ExportSource, ExportService } from './export.service';

const items: ExportTreeItem[] = [
  { id: 'thesis', name: 'Thesis', type: 'Folder', parentId: null },
  { id: 'one', name: 'One', type: 'Document', parentId: 'thesis' },
  { id: 'two', name: 'Two', type: 'Document', parentId: 'thesis' },
  { id: 'empty', name: 'Blank page', type: 'Document', parentId: null },
];

function docFor(source: ExportSource, setup: PageSetup, warnings: ExportDocument['warnings'] = []): ExportDocument {
  return {
    title: source.title,
    setup,
    citationStyle: { id: setup.citationStyle, label: 'APA 7', kind: 'in-text' },
    sections: [],
    footnotes: [],
    bibliography: null,
    warnings,
    words: 42,
  };
}

describe('ExportDialogComponent (issue #1164)', () => {
  let fixture: ComponentFixture<ExportDialogComponent>;
  let exporter: {
    load: ReturnType<typeof vi.fn>;
    savedSetup: ReturnType<typeof vi.fn>;
    saveSetup: ReturnType<typeof vi.fn>;
    resolve: ReturnType<typeof vi.fn>;
    download: ReturnType<typeof vi.fn>;
  };
  let warnings: ExportDocument['warnings'];

  async function open(documentId: string, markdown: string | null = null): Promise<void> {
    fixture = TestBed.createComponent(ExportDialogComponent);
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('documentId', documentId);
    fixture.componentRef.setInput('currentMarkdown', markdown);
    fixture.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await new Promise((resolve) => setTimeout(resolve, 200));
      fixture.detectChanges();
      await fixture.whenStable();
    }
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  beforeEach(async () => {
    localStorage.removeItem('nostos.export.format');
    warnings = [];
    exporter = {
      load: vi.fn(async (_items: ExportTreeItem[], id: string, overrides: Map<string, string>): Promise<ExportSource> => {
        const target = items.find((i) => i.id === id)!;
        const docs = target.type === 'Folder' ? items.filter((i) => i.parentId === id) : [target];
        return {
          targetId: id,
          title: target.name,
          type: target.type,
          kept: [],
          chapters: docs.map((d) => ({ id: d.id, title: d.name, groupPath: [], markdown: overrides.get(d.id) ?? (d.id === 'empty' ? '' : `Words of ${d.name}.`) })),
        };
      }),
      savedSetup: vi.fn(async () => null),
      saveSetup: vi.fn(async () => undefined),
      resolve: vi.fn(async (source: ExportSource, setup: PageSetup) => docFor(source, setup, warnings)),
      download: vi.fn(async () => undefined),
    };
    await TestBed.configureTestingModule({
      imports: [ExportDialogComponent],
      providers: [
        { provide: ExportService, useValue: exporter },
        { provide: ToastService, useValue: { show: vi.fn() } },
        {
          provide: StudioPreferencesService,
          useValue: { preferences: signal({ ...DEFAULT_STUDIO_PREFERENCES, font: 'lora', citationStyle: 'mla' }) },
        },
      ],
    }).compileComponents();
  });

  afterEach(() => fixture?.destroy());

  it('starts from the Studio’s preferences, downloads under the title, and saves the setup with the writing', async () => {
    await open('one');

    const component = fixture.componentInstance;
    expect(component.setup()).toMatchObject({ font: 'lora', citationStyle: 'mla', marginMm: 25.4 });
    // A Word document is what a writer hands in most often.
    expect(component.format().id).toBe('docx');
    expect(el('export-download')?.textContent?.trim()).toBe('Download');
    // No file name field: the title names the file.
    expect(fixture.nativeElement.querySelector('input[type="text"]')).toBeNull();

    const download = el('export-download') as HTMLButtonElement;
    expect(download.disabled).toBe(false);
    download.click();
    await settle();

    expect(exporter.download).toHaveBeenCalledWith(expect.objectContaining({ title: 'One' }), expect.objectContaining({ id: 'docx' }), 'One.docx');
    expect(exporter.saveSetup).toHaveBeenCalledWith('one', expect.objectContaining({ font: 'lora', citationStyle: 'mla' }));
  });

  it('starts the next export in the format used last', async () => {
    await open('one');
    fixture.componentInstance.chooseFormat('markdown');
    await settle();
    (el('export-download') as HTMLButtonElement).click();
    await settle();
    expect(exporter.download).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ id: 'markdown' }), 'One.md');
    fixture.destroy();

    await open('two');
    expect(fixture.componentInstance.format().id).toBe('markdown');
  });

  it('copies rich text from the click itself, and says so', async () => {
    const write = vi.fn(async () => undefined);
    const restore = stubClipboard(write);
    try {
      await open('one');
      fixture.componentInstance.chooseFormat('rich-text');
      await settle();
      expect(el('export-download')?.textContent?.trim()).toBe('Copy');
      expect(el('export-format-hint')?.textContent).toContain('Substack, Google Docs or Word');
      expect(el('export-setup-summary')?.textContent?.trim()).toBe('MLA 9');

      (el('export-download') as HTMLButtonElement).click();
      // Written before anything is awaited: browsers allow the clipboard only during the click.
      expect(write).toHaveBeenCalledTimes(1);
      await settle();
      expect(TestBed.inject(ToastService).show).toHaveBeenCalledWith(expect.stringContaining('Copied'), 'success');
      expect(exporter.download).not.toHaveBeenCalled();
    } finally {
      restore();
    }
  });

  it('offers the Markdown file when the browser refuses the clipboard', async () => {
    const restore = stubClipboard(vi.fn(async () => Promise.reject(new DOMException('No', 'NotAllowedError'))));
    try {
      await open('one');
      fixture.componentInstance.chooseFormat('rich-text');
      await settle();
      (el('export-download') as HTMLButtonElement).click();
      await settle();

      const refused = el('export-copy-refused')!;
      expect(refused.getAttribute('role')).toBe('alert');
      expect(refused.textContent).toContain('didn’t allow copying');
      (refused.querySelector('button') as HTMLButtonElement).click();
      await settle();
      expect(exporter.download).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ id: 'markdown' }), 'One.md');
    } finally {
      restore();
    }
  });

  it('opens the print dialog for a PDF, named from the title', async () => {
    const pdf = EXPORT_FORMATS.find((f) => f.id === 'pdf') as Extract<ExportFormat, { action: 'print' }>;
    const print = vi.spyOn(pdf, 'print').mockResolvedValue(undefined);
    try {
      await open('one');
      fixture.componentInstance.chooseFormat('pdf');
      await settle();
      expect(el('export-download')?.textContent?.trim()).toBe('Save as PDF…');
      expect(el('export-format-hint')?.textContent).toContain('Save as PDF');
      (el('export-download') as HTMLButtonElement).click();
      await settle();
      expect(print).toHaveBeenCalledWith(expect.objectContaining({ title: 'One' }), 'One');
      expect(exporter.download).not.toHaveBeenCalled();
    } finally {
      print.mockRestore();
    }
  });

  it('lists what the format alone can’t carry before the download, and waits for it', async () => {
    let finish!: (warnings: ExportDocument['warnings']) => void;
    const docx = EXPORT_FORMATS.find((f) => f.id === 'docx')!;
    const check = vi.spyOn(docx, 'check').mockImplementation(() => new Promise((resolve) => (finish = resolve)));
    try {
      await open('one');
      expect((el('export-download') as HTMLButtonElement).disabled).toBe(true);
      expect(el('export-status')?.textContent).toContain('Checking the images');

      finish([{ kind: 'image', message: 'Image “Map”: couldn’t be copied into the file, shown as [image: Map]', chapter: null, count: 1 }]);
      await settle();
      expect((el('export-download') as HTMLButtonElement).disabled).toBe(false);
      expect(el('export-warnings-toggle')?.textContent?.trim()).toBe('1 thing to check before you send this');
    } finally {
      check.mockRestore();
    }
  });

  it('uses the page setup saved with the writing', async () => {
    exporter.savedSetup.mockResolvedValueOnce({ paper: 'letter', marginMm: 12.7, font: 'inter', lineSpacing: 2, citationStyle: 'apa' });
    await open('one');
    expect(fixture.componentInstance.setup()).toEqual({ paper: 'letter', marginMm: 12.7, font: 'inter', lineSpacing: 2, citationStyle: 'apa' });
  });

  it('cannot download an empty writing, and says why', async () => {
    await open('empty');

    expect((el('export-download') as HTMLButtonElement).disabled).toBe(true);
    expect(el('export-status')?.textContent).toContain('This writing is empty');
    expect(exporter.resolve).not.toHaveBeenCalled();
  });

  it('says what to check before sending in one quiet line that opens to short items', async () => {
    warnings = [
      { kind: 'citation', message: '(Lost, 1900): book no longer in your library, kept as written', chapter: 'One', count: 2 },
      { kind: 'image', message: 'Image “Map”: couldn’t be loaded, shown as [image: Map]', chapter: null, count: 1 },
    ];
    await open('one');

    const toggle = el('export-warnings-toggle') as HTMLButtonElement;
    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle.textContent?.trim()).toBe('2 things to check before you send this');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.classList).toContain('nostos-button--link');
    const caret = toggle.querySelector('nostos-icon.export-caret')!;
    expect(caret.classList).not.toContain('is-open');
    expect(el('export-warnings')?.querySelector('li')).toBeNull();

    toggle.click();
    fixture.detectChanges();
    const items = [...el('export-warnings')!.querySelectorAll('li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(items).toEqual([
      'One · (Lost, 1900): book no longer in your library, kept as written (2 times)',
      'Image “Map”: couldn’t be loaded, shown as [image: Map]',
    ]);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(caret.classList).toContain('is-open');
    expect((el('export-download') as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps the setup in one collapsed row; Markdown has no pages, so only the citation style', async () => {
    await open('one');
    fixture.componentInstance.chooseFormat('markdown');
    await settle();

    expect(el('export-setup')?.textContent).toContain('Citation style');
    expect(el('export-setup-summary')?.textContent?.trim()).toBe('MLA 9');
    expect(el('export-style')).toBeNull();

    (el('export-setup-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
    // Opened, the row's value is the control itself: one row, no second "Citation style".
    expect(el('export-setup')!.querySelector('[data-testid="export-style"]')).toBeTruthy();
    expect(el('export-setup-summary')).toBeNull();
    expect(el('export-setup-toggle')?.textContent?.trim()).toBe('Done');
    expect(fixture.nativeElement.textContent.match(/Citation style/g)).toHaveLength(1);
    expect(el('export-paper')).toBeNull();
  });

  it('names the house style as such', async () => {
    exporter.savedSetup.mockResolvedValueOnce({ paper: 'a4', marginMm: 25.4, font: 'lora', lineSpacing: 1.5, citationStyle: 'nostos' });
    await open('one');
    expect(el('export-setup-summary')?.textContent?.trim()).toMatch(/· Nostos \(house style\)$/);
  });

  it('offers paper, margins, font and spacing for a format with pages', async () => {
    for (const id of ['docx', 'pdf']) {
      await open('one');
      fixture.componentInstance.chooseFormat(id);
      await settle();
      expect(el('export-setup')?.textContent).toContain('Page');
      // The paper follows the test browser's locale.
      expect(el('export-setup-summary')?.textContent?.trim()).toMatch(/^(A4|Letter), normal margins, Lora, 1.5 · MLA 9$/);

      (el('export-setup-toggle') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(el('export-setup')!.querySelector('[data-testid="export-style"]')).toBeTruthy();
      expect(el('export-paper')).toBeTruthy();
      expect(fixture.componentInstance.filename()).toBe(`One.${id}`);
      fixture.destroy();
    }
  });

  it('exports the folder the writing is in, with its chapters reorderable for this export only', async () => {
    await open('one', 'Unsaved words.');
    const component = fixture.componentInstance;

    component.chooseTarget('thesis');
    await settle();
    expect(exporter.load).toHaveBeenLastCalledWith(items, 'thesis', new Map([['one', 'Unsaved words.']]));
    const names = () => [...(el('export-chapters')?.querySelectorAll('.export-chapter-name') ?? [])].map((n) => n.textContent?.trim());
    expect(names()).toEqual(['One', 'Two']);
    expect(fixture.componentInstance.filename()).toBe('Thesis.docx');
    // Arrows only, no drag handles.
    expect(el('export-chapters')!.querySelector('[cdkDragHandle], .export-chapter-grip')).toBeNull();

    (el('export-chapters')!.querySelector('[aria-label="Move One later"]') as HTMLButtonElement).click();
    await settle();
    expect(names()).toEqual(['Two', 'One']);
    expect(exporter.resolve).toHaveBeenLastCalledWith(expect.objectContaining({ targetId: 'thesis' }), expect.anything(), ['two', 'one'], expect.anything());

    (el('export-download') as HTMLButtonElement).click();
    await settle();
    expect(exporter.download).toHaveBeenCalledWith(expect.anything(), expect.anything(), 'Thesis.docx');
    expect(exporter.saveSetup).toHaveBeenCalledWith('thesis', expect.anything());
  });

  it('re-resolves when the citation style changes, not for the paper', async () => {
    await open('one');
    const calls = exporter.resolve.mock.calls.length;

    fixture.componentInstance.updateSetup({ paper: 'letter' });
    await settle();
    expect(exporter.resolve.mock.calls.length).toBe(calls);

    fixture.componentInstance.setCitationStyle('chicago-notes');
    await settle();
    expect(exporter.resolve.mock.calls.length).toBe(calls + 1);
    expect(exporter.resolve).toHaveBeenLastCalledWith(expect.anything(), expect.objectContaining({ citationStyle: 'chicago-notes' }), null, expect.anything());
  });
});

/** Puts a clipboard that `write` stands in for on the test browser, and returns how to take it away. */
function stubClipboard(write: (items: ClipboardItem[]) => Promise<void>): () => void {
  const had = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
  const hadItem = (globalThis as { ClipboardItem?: unknown }).ClipboardItem;
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { write } });
  (globalThis as { ClipboardItem?: unknown }).ClipboardItem ??= class {
    constructor(readonly items: Record<string, Blob>) {}
  };
  return () => {
    if (had) Object.defineProperty(navigator, 'clipboard', had);
    else delete (navigator as { clipboard?: unknown }).clipboard;
    (globalThis as { ClipboardItem?: unknown }).ClipboardItem = hadItem;
  };
}
