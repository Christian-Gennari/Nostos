import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { Editor } from '@tiptap/core';

import { MarkdownEditorComponent } from './markdown-editor.component';
import { CitationRef, citationMarkdown, citationTextSpec, parseCitationHref } from './citation.extension';
import { CitationRenderer } from './live-citations';
import { htmlToMarkdown, markdownToHtml } from './markdown-conversion';

const NAMES: Record<string, string> = { b1: 'Voltaire', b2: 'Kant', b3: 'Weil' };

/** A stand-in for the Studio's renderer: predictable words for each place a citation stands. */
function renderer(placement: CitationRenderer['placement'], year = '1992'): CitationRenderer {
  const page = (ref: CitationRef, prefix: string) => (ref.page ? `${prefix}${ref.page}` : '');
  return {
    placement,
    bibliographyHeading: placement === 'note' ? 'Bibliography' : 'References',
    bibliographyBooks: [],
    inText: (ref) =>
      placement === 'house'
        ? `*See: ${NAMES[ref.book]}${page(ref, ', p. ')}*`
        : `(${NAMES[ref.book]}, ${year}${page(ref, ', p. ')})`,
    note: (ref, subsequent) =>
      subsequent ? `${NAMES[ref.book]}, *Short*${page(ref, ', ')}.` : `${NAMES[ref.book]}, *Full* (Knopf, ${year})${page(ref, ', ')}.`,
    bibliography: (books) => [...books].sort((a, b) => NAMES[a].localeCompare(NAMES[b])).map((book) => ({ book, text: `${NAMES[book]}. *Book*. ${year}.` })),
  };
}

const IN_TEXT = [
  'Opening line.',
  '> To be is to do.\n> \n> [(Voltaire, 1992, p. 42)](nostos:cite/b1?p=42&as=quote&src=n1)',
  'My own thought [(Kant, 1992)](nostos:cite/b2?as=note)',
  'As argued [(Voltaire, 1992)](nostos:cite/b1?as=reference) here.',
  '## References',
  '[Kant. *Book*. 1992.](nostos:cite/b2?as=bib)',
  '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
].join('\n\n');

describe('live citations (issue #1157)', () => {
  let fixture: ComponentFixture<MarkdownEditorComponent>;
  let component: MarkdownEditorComponent;
  let signatures: string[];

  async function create(markdown: string): Promise<void> {
    await TestBed.configureTestingModule({ imports: [MarkdownEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(MarkdownEditorComponent);
    component = fixture.componentInstance;
    signatures = [];
    component.citationsChange.subscribe((signature) => signatures.push(signature));
    fixture.componentRef.setInput('initialContent', markdown);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  afterEach(() => fixture?.destroy());

  function editor(): Editor {
    return (component as unknown as { editor: Editor }).editor;
  }

  function markdown(): string {
    return component.currentMarkdown().trim();
  }

  describe('in Markdown', () => {
    it('reads and writes a citation link unchanged, italics and all', () => {
      const source = 'See [Voltaire, *Candide* \\[2nd ed.\\]](nostos:cite/b1?p=4&as=reference&src=n1) now.';
      const html = markdownToHtml(source);
      expect(html).toContain('<cite');
      expect(html).not.toContain('<a ');
      expect(htmlToMarkdown(html).trim()).toBe(source);
    });

    it('leaves an ordinary link alone, and reads only its own addresses', () => {
      expect(markdownToHtml('[site](https://example.com)')).toContain('<a href="https://example.com"');
      expect(parseCitationHref('nostos:cite/b1?p=0&as=bogus')).toEqual({ book: 'b1', page: null, src: null, as: null });
      expect(parseCitationHref('nostos:cite/../x')).toBeNull();
      expect(citationMarkdown(' (Kant, 1992) ', { book: 'b2', page: 3, src: null, as: 'quote' })).toBe(
        '[(Kant, 1992)](nostos:cite/b2?p=3&as=quote)',
      );
    });

    it('draws the citation words with their italics, unescaped', () => {
      expect(citationTextSpec('Voltaire, *Candide \\[x\\]*, 4')).toEqual(['Voltaire, ', ['em', {}, 'Candide [x]'], ', 4']);
      expect(citationTextSpec('an *open marker')).toEqual(['an ', '*', 'open marker']);
    });
  });

  it('keeps each citation one atom in the editor and saves the writing unchanged', async () => {
    await create(IN_TEXT);

    const cites = fixture.nativeElement.querySelectorAll('cite.citation');
    expect(cites.length).toBe(5);
    expect(cites[0].textContent).toBe('(Voltaire, 1992, p. 42)');
    expect(component.citedBooks()).toEqual(['b1', 'b2']);
    expect(component.hasBibliography()).toBe(true);
    expect(signatures.length).toBe(1);
    expect(component.refreshCitations(renderer('in-text'))).toBe(false);
    expect(markdown()).toBe(IN_TEXT);
  });

  it('writes every citation again when a book gains its year, the bibliography too', async () => {
    await create(IN_TEXT.replace(/1992/g, 'n.d.'));

    expect(component.refreshCitations(renderer('in-text'))).toBe(true);
    expect(markdown()).not.toContain('n.d.');
    expect(markdown()).toContain('[(Voltaire, 1992, p. 42)](nostos:cite/b1?p=42&as=quote&src=n1)');
    expect(markdown()).toContain('[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)');
  });

  it('moves citations into footnotes for a note style, and back again unchanged', async () => {
    await create(IN_TEXT);
    const before = markdown();

    expect(component.refreshCitations(renderer('note'))).toBe(true);
    expect(markdown()).toBe(
      [
        'Opening line.',
        '> To be is to do.[^1]',
        'My own thought[^2]',
        'As argued[^3] here.',
        '[^1]: [Voltaire, *Full* (Knopf, 1992), 42.](nostos:cite/b1?p=42&as=quote&src=n1)',
        '[^2]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=note)',
        // Voltaire's second note is the short one.
        '[^3]: [Voltaire, *Short*.](nostos:cite/b1?as=reference)',
        '## Bibliography',
        '[Kant. *Book*. 1992.](nostos:cite/b2?as=bib)',
        '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
      ].join('\n\n'),
    );

    expect(component.refreshCitations(renderer('in-text'))).toBe(true);
    expect(markdown()).toBe(before);
  });

  it('gives the house format its own lines for a quote and a note', async () => {
    await create('Thought.[^1]\n\n[^1]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=note)');

    expect(component.refreshCitations(renderer('house'))).toBe(true);
    expect(markdown()).toBe('Thought.\n\n[*See: Kant*](nostos:cite/b2?as=note)');
  });

  it('keeps a note the writer wrote, and leaves a citation inside it where it is', async () => {
    await create('A claim.[^1]\n\n[^1]: Compare [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference) with Hume.');

    component.refreshCitations(renderer('in-text'));
    expect(markdown()).toBe('A claim.[^1]\n\n[^1]: Compare [(Kant, 1992)](nostos:cite/b2?as=reference) with Hume.');
  });

  it('drops the Studio’s note with its marker and renumbers the rest; a written note stays', async () => {
    await create(
      [
        'One[^1] two[^2] three[^3].',
        '[^1]: My own aside.',
        '[^2]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference)',
        '[^3]: [Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=reference)',
      ].join('\n\n'),
    );
    const doc = editor().state.doc;
    const markers: number[] = [];
    doc.descendants((node, pos) => {
      if (node.type.name === 'footnoteRef' && pos < doc.child(0).nodeSize) markers.push(pos);
    });

    // The second marker goes: its note goes with it, and three becomes two.
    editor().chain().deleteRange({ from: markers[1], to: markers[1] + 1 }).run();
    expect(markdown()).toBe(
      ['One[^1] two three[^2].', '[^1]: My own aside.', '[^2]: [Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=reference)'].join('\n\n'),
    );

    // The first goes: the writer's own note is kept, numbered after the others.
    editor().chain().deleteRange({ from: markers[0], to: markers[0] + 1 }).run();
    expect(markdown()).toBe(
      ['One two three[^1].', '[^1]: [Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=reference)', '[^2]: My own aside.'].join('\n\n'),
    );

    // One undo brings the marker and its numbering back together.
    editor().commands.undo();
    expect(markdown()).toContain('One[^1] two three[^2].');
  });

  it('puts a quote and its footnote in together, numbered in reading order, and takes both out with one undo', async () => {
    await create('Start.\n\nEnd.[^1]\n\n[^1]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference)');
    editor().commands.setTextSelection(3);

    await component.insertMarkdown('Quoted words.[^2]', {
      number: component.nextFootnoteNumber(),
      markdown: '[Voltaire, *Full* (Knopf, 1992).](nostos:cite/b1?as=quote)',
    });
    expect(markdown()).toContain('Quoted words.[^1]');
    expect(markdown()).toContain('End.[^2]');
    expect(markdown()).toMatch(/\[\^1\]: \[Voltaire[^\n]*\n\n\[\^2\]: \[Kant/);

    editor().commands.undo();
    expect(markdown()).toBe('Start.\n\nEnd.[^1]\n\n[^1]: [Kant, *Full* (Knopf, 1992).](nostos:cite/b2?as=reference)');
  });

  it('adds a newly cited book to the bibliography and puts footnotes ahead of it', async () => {
    await create(['Weil says [(Weil, 1992)](nostos:cite/b3?as=reference).', '## References', '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)'].join('\n\n'));

    component.refreshCitations(renderer('in-text'));
    expect(markdown()).toBe(
      [
        'Weil says [(Weil, 1992)](nostos:cite/b3?as=reference).',
        '## References',
        '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
        '[Weil. *Book*. 1992.](nostos:cite/b3?as=bib)',
      ].join('\n\n'),
    );

    component.refreshCitations(renderer('note'));
    expect(markdown()).toBe(
      [
        'Weil says[^1].',
        '[^1]: [Weil, *Full* (Knopf, 1992).](nostos:cite/b3?as=reference)',
        '## Bibliography',
        '[Voltaire. *Book*. 1992.](nostos:cite/b1?as=bib)',
        '[Weil. *Book*. 1992.](nostos:cite/b3?as=bib)',
      ].join('\n\n'),
    );
  });

  it('says when the citations change, and not for an edit elsewhere', async () => {
    await create('Text [(Kant, 1992)](nostos:cite/b2?as=reference).');
    const start = signatures.length;

    await component.insertMarkdown('More words.');
    expect(signatures.length).toBe(start);

    await component.insertMarkdown('[(Weil, 1992)](nostos:cite/b3?as=reference)');
    expect(signatures.length).toBe(start + 1);
    expect([...component.citedBooks()].sort()).toEqual(['b2', 'b3']);
  });
});
