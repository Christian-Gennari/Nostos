import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ToastService } from '../../core/services/toast.service';
import { DEFAULT_STUDIO_PREFERENCES, StudioPreferencesService } from '../page-settings/studio-preferences.service';
import { signal } from '@angular/core';
import { ExportDialogComponent } from './export-dialog.component';
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
    expect((el('export-filename') as HTMLInputElement).value).toBe('One');
    expect(el('export-format')?.textContent).toContain('Web page');

    const download = el('export-download') as HTMLButtonElement;
    expect(download.disabled).toBe(false);
    download.click();
    await settle();

    expect(exporter.download).toHaveBeenCalledWith(expect.objectContaining({ title: 'One' }), expect.objectContaining({ id: 'html' }), 'One.html');
    expect(exporter.saveSetup).toHaveBeenCalledWith('one', expect.objectContaining({ font: 'lora', citationStyle: 'mla' }));
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

  it('lists what the writer should know before downloading', async () => {
    warnings = [{ kind: 'citation', message: '“(Lost, 1900)” could not be written.', chapter: 'One', count: 2 }];
    await open('one');

    const list = el('export-warnings');
    expect(list?.textContent).toContain('Before you download');
    expect(list?.textContent).toContain('One:');
    expect(list?.textContent).toContain('(2 times)');
    expect((el('export-download') as HTMLButtonElement).disabled).toBe(false);
  });

  it('exports the folder the writing is in, with its chapters reorderable for this export only', async () => {
    await open('one', 'Unsaved words.');
    const component = fixture.componentInstance;

    component.chooseTarget('thesis');
    await settle();
    expect(exporter.load).toHaveBeenLastCalledWith(items, 'thesis', new Map([['one', 'Unsaved words.']]));
    const names = () => [...(el('export-chapters')?.querySelectorAll('.export-chapter-name') ?? [])].map((n) => n.textContent?.trim());
    expect(names()).toEqual(['One', 'Two']);
    expect((el('export-filename') as HTMLInputElement).value).toBe('Thesis');

    (el('export-chapters')!.querySelector('[aria-label="Move One later"]') as HTMLButtonElement).click();
    await settle();
    expect(names()).toEqual(['Two', 'One']);
    expect(exporter.resolve).toHaveBeenLastCalledWith(expect.objectContaining({ targetId: 'thesis' }), expect.anything(), ['two', 'one'], expect.anything());

    (el('export-download') as HTMLButtonElement).click();
    await settle();
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
