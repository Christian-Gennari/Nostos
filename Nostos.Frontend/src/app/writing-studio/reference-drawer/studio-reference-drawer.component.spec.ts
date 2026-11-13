import { Component, Input } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { marked } from 'marked';
import { Note } from '../../core/dtos/note.dtos';
import { NOSTOS_ICONS } from '../../ui/icon/nostos-icons';
import { NoteItemComponent } from '../../ui/note-item/note-item.component';
import { clearReferencePreviewCache } from '../reference-preview';
import { StudioReferenceDrawerComponent } from './studio-reference-drawer.component';

@Component({ selector: 'app-note-item', standalone: true, template: '' })
class NoteItemStub {
  @Input() note: unknown;
  @Input() topicMap: Map<string, unknown> = new Map();
  @Input() readOnly = false;
  @Input() showMeta = true;
  @Input() passageViewerOptions: unknown = null;
}

describe('StudioReferenceDrawerComponent', () => {
  let fixture: ComponentFixture<StudioReferenceDrawerComponent>;
  let component: StudioReferenceDrawerComponent;

  const sampleNote: Note = {
    id: 'n-1',
    bookId: 'b-1',
    content: '**Thoughtful** note with [[Topic A]]',
    createdAt: '2026-01-01T00:00:00Z',
    bookTitle: 'The Book',
    sourceAnchorKind: 'pdf_page',
    sourceAnchorValue: '12',
    anchorVerified: true,
  };

  beforeEach(async () => {
    clearReferencePreviewCache();
    TestBed.overrideComponent(StudioReferenceDrawerComponent, {
      remove: { imports: [NoteItemComponent] },
      add: { imports: [NoteItemStub] },
    });

    await TestBed.configureTestingModule({
      imports: [StudioReferenceDrawerComponent],
    }).compileComponents();

    expect(NOSTOS_ICONS).toBeDefined();
    fixture = TestBed.createComponent(StudioReferenceDrawerComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    vi.restoreAllMocks();
    fixture?.destroy();
  });

  it('precomputes referencePreview once per note list and does not re-parse Markdown across detectChanges passes', () => {
    const parseSpy = vi.spyOn(marked, 'parse');

    fixture.componentRef.setInput('hasActiveItem', true);
    fixture.componentRef.setInput('keptSourcesState', 'loaded');
    fixture.componentRef.setInput('keptNotes', [sampleNote]);
    fixture.detectChanges();

    expect(parseSpy).toHaveBeenCalledTimes(1);
    const previewEl = fixture.nativeElement.querySelector('.reference-source-preview') as HTMLElement;
    expect(previewEl.textContent?.trim()).toBe('Thoughtful note with Topic A');

    // Subsequent unrelated input changes & detectChanges passes must not re-parse existing notes.
    fixture.componentRef.setInput('editorWordCount', 42);
    fixture.detectChanges();
    fixture.componentRef.setInput('editorWordCount', 43);
    fixture.detectChanges();

    expect(parseSpy).toHaveBeenCalledTimes(1);
  });

  it('emits sourceInspected and keptSourceRemoved without bubbling inspection on remove click', () => {
    const inspected: Note[] = [];
    const removed: string[] = [];
    component.sourceInspected.subscribe((n) => inspected.push(n));
    component.keptSourceRemoved.subscribe((id) => removed.push(id));

    fixture.componentRef.setInput('hasActiveItem', true);
    fixture.componentRef.setInput('keptSourcesState', 'loaded');
    fixture.componentRef.setInput('keptNotes', [sampleNote]);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.reference-row-action') as HTMLButtonElement).click();
    expect(removed).toEqual(['n-1']);
    expect(inspected).toEqual([]);

    (fixture.nativeElement.querySelector('.reference-source-row-main') as HTMLButtonElement).click();
    expect(inspected).toHaveLength(1);
    expect(inspected[0].id).toBe('n-1');
  });
});
