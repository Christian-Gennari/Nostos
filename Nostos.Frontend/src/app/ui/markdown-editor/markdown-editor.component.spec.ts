import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  MarkdownEditorComponent,
  caretScrollDelta,
  countWords,
  looksLikeMarkdown,
} from './markdown-editor.component';

/**
 * The Studio page on TipTap (issue #1037). These run the real editor in jsdom
 * and assert the contract the Studio relies on: Markdown in and out, insertion
 * at the caret, word counts, and the caret/scroll handoff.
 */
describe('MarkdownEditorComponent', () => {
  let fixture: ComponentFixture<MarkdownEditorComponent>;
  let component: MarkdownEditorComponent;
  let emitted: string[];
  let counts: number[];

  async function create(markdown: string): Promise<void> {
    await TestBed.configureTestingModule({ imports: [MarkdownEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(MarkdownEditorComponent);
    component = fixture.componentInstance;
    emitted = [];
    counts = [];
    component.contentChange.subscribe((md) => emitted.push(md));
    component.wordCountChange.subscribe((n) => counts.push(n));
    fixture.componentRef.setInput('initialContent', markdown);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  afterEach(() => fixture?.destroy());

  function page(): HTMLElement {
    return fixture.nativeElement.querySelector('.nostos-manuscript-body') as HTMLElement;
  }

  it('renders the Markdown it is given in the app DOM, not an iframe', async () => {
    await create('# Title\n\nSome **bold** prose.');

    expect(fixture.nativeElement.querySelector('iframe')).toBeNull();
    expect(page().querySelector('h1')?.textContent).toBe('Title');
    expect(page().querySelector('strong')?.textContent).toBe('bold');
    expect(component.editorStarting()).toBe(false);
  });

  it('reports the word count on start', async () => {
    await create('One two three. Four.');
    expect(counts.at(-1)).toBe(4);
  });

  it('inserts Markdown at the caret and emits the whole document as Markdown', async () => {
    await create('Before.');

    const inserted = await component.insertMarkdown('> A quote.\n> — *A Book*');

    expect(inserted).toBe(true);
    const md = emitted.at(-1) ?? '';
    expect(md).toContain('Before.');
    expect(md).toContain('> A quote.');
    expect(page().querySelector('blockquote em')?.textContent).toBe('A Book');
  });

  it('refuses an empty insertion instead of inventing content', async () => {
    await create('Text.');
    expect(await component.insertMarkdown('   ')).toBe(false);
  });

  it('refuses to insert once destroyed', async () => {
    await create('Text.');
    fixture.destroy();
    expect(await component.insertMarkdown('More.')).toBe(false);
  });

  it('replaces the page when another document is opened', async () => {
    await create('First document.');

    fixture.componentRef.setInput('initialContent', '# Second\n\nOther text.');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(page().querySelector('h1')?.textContent).toBe('Second');
    expect(page().textContent).not.toContain('First document.');
  });

  it('does not reset the page when the save echoes the same Markdown back', async () => {
    await create('Stable text.');
    const before = page().querySelector('p');

    fixture.componentRef.setInput('initialContent', 'Stable text.\n');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(page().querySelector('p')).toBe(before);
  });

  it('round-trips the caret through capture and restore', async () => {
    await create('Alpha beta gamma.');
    await component.insertMarkdown('Inserted.');
    const state = component.captureTransientState();
    expect(state?.bookmark).toEqual(expect.objectContaining({ from: expect.any(Number) }));

    const restored = await component.restoreTransientState(state!, 'Alpha beta gamma.');
    expect(restored).toBe(true);
  });

  it('waits to restore until the expected document is the one on the page', async () => {
    await create('Doc A.');
    let settled: boolean | null = null;
    void component
      .restoreTransientState({ bookmark: { from: 1, to: 1 } }, 'Doc B.')
      .then((r) => (settled = r));
    await Promise.resolve();
    expect(settled).toBeNull();

    fixture.componentRef.setInput('initialContent', 'Doc B.');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(settled).toBe(true);
  });

  it('clamps a stale caret position instead of throwing', async () => {
    await create('Short.');
    const restored = await component.restoreTransientState(
      { bookmark: { from: 9999, to: 9999 } },
      'Short.',
    );
    expect(restored).toBe(true);
  });

  it('offers no underline control, because Markdown cannot keep one', async () => {
    await create('Text.');
    const labels = [...fixture.nativeElement.querySelectorAll('.editor-toolbar [aria-label]')].map(
      (el: Element) => el.getAttribute('aria-label'),
    );
    expect(labels).not.toContain('Underline');
    expect(labels).toEqual(expect.arrayContaining(['Bold', 'Italic', 'Link', 'Image']));
  });

  it('toggles bold from the toolbar and saves it as Markdown', async () => {
    await create('word');
    (component as unknown as { editor: { commands: { selectAll: () => void } } }).editor.commands.selectAll();
    component.toggleBold();
    expect(emitted.at(-1)).toBe('**word**');
  });

  it('sets a link through the inline URL field', async () => {
    await create('link me');
    (component as unknown as { editor: { commands: { selectAll: () => void } } }).editor.commands.selectAll();
    component.openUrlPrompt('link');
    component.urlValue.set('https://example.com');
    component.applyUrl();
    expect(emitted.at(-1)).toBe('[link me](https://example.com)');
  });

  it('does not undo back into the previous document after switching', async () => {
    await create('First document.');
    await component.insertMarkdown('Typed in the first.');

    fixture.componentRef.setInput('initialContent', 'Second document.');
    fixture.detectChanges();
    await fixture.whenStable();
    emitted = [];
    component.undo();

    expect(page().textContent).toContain('Second document.');
    expect(page().textContent).not.toContain('First document.');
    expect(emitted.every((md) => !md.includes('First document.'))).toBe(true);
  });

  it('keeps deeper headings a writing already has', async () => {
    await create('#### Fourth level\n\nText.');
    expect(page().querySelector('h4')?.textContent).toBe('Fourth level');
    expect(component.currentMarkdown()).toContain('#### Fourth level');
  });

  it('keeps an inline data-URI image a writing already has', async () => {
    const png = 'data:image/png;base64,iVBORw0KGgo=';
    await create(`![dot](${png})`);
    expect(page().querySelector('img')?.getAttribute('src')).toBe(png);
    expect(component.currentMarkdown()).toContain(png);
  });

  it('inserts the URL as linked text when nothing is selected', async () => {
    await create('');
    component.openUrlPrompt('link');
    component.urlValue.set('https://example.com');
    component.applyUrl();
    expect(emitted.at(-1)).toBe('[https://example.com](https://example.com)');
  });

  describe('the @ source query (#1039)', () => {
    function typeText(text: string) {
      const editor = (component as unknown as { editor: import('@tiptap/core').Editor }).editor;
      for (const ch of text) editor.commands.insertContent(ch);
    }

    it('reports the query typed after @ and when it closes', async () => {
      await create('Some prose.');
      const states: unknown[] = [];
      component.sourceSuggestion.subscribe((s) => states.push(s));
      (component as any).editor.commands.focus('end');
      typeText(' @schop');
      expect(states.at(-1)).toEqual(expect.objectContaining({ query: 'schop' }));
      (component as any).editor.commands.insertContent('  ');
      expect(states.at(-1)).toBeNull();
    });

    it('does not open inside a word, so an email address stays prose', async () => {
      await create('Mail');
      const states: unknown[] = [];
      component.sourceSuggestion.subscribe((s) => states.push(s));
      (component as any).editor.commands.focus('end');
      typeText(' me at reader@example');
      expect(states.filter(Boolean)).toEqual([]);
    });

    it('puts a block quote after the paragraph, never inside the sentence', async () => {
      await create('First paragraph. Second paragraph follows.');
      (component as any).editor.commands.focus('end');
      typeText(' See @schop');
      expect(component.replaceSourceQuery('> A quote.\n>\n> — Author, *Book*', 'block')).toBe(true);
      const md = component.currentMarkdown();
      expect(md).toContain('First paragraph. Second paragraph follows. See');
      expect(md).not.toContain('@schop');
      expect(md).toMatch(/follows\. See\s*\n\n> A quote\./);
    });

    it('puts a reference inline, and the next words are not italic', async () => {
      await create('As');
      (component as any).editor.commands.focus('end');
      typeText(' @tal');
      expect(component.replaceSourceQuery('*See: Author, Book*', 'inline')).toBe(true);
      typeText(' is right.');
      expect(component.currentMarkdown()).toBe('As _See: Author, Book_ is right.');
    });

    it('opens with the shortcut and takes its @ back when closed empty', async () => {
      await create('Word');
      (component as any).editor.commands.focus('end');
      expect(component.openSourceQuery()).toBe(true);
      expect(component.currentMarkdown()).toBe('Word @');
      component.closeSourceQuery();
      await Promise.resolve();
      expect(component.currentMarkdown().trim()).toBe('Word');
    });
  });

  it('emits the final Markdown when it is torn down', async () => {
    await create('Last words.');
    emitted = [];
    fixture.destroy();
    expect(emitted).toEqual(['Last words.']);
  });
});

describe('caretScrollDelta (typewriter geometry)', () => {
  it('stays put inside a one-line deadband around 45%', () => {
    expect(caretScrollDelta(450, 1000)).toBeNull();
    expect(caretScrollDelta(480, 1000)).toBeNull();
    expect(caretScrollDelta(420, 1000)).toBeNull();
  });

  it('scrolls the caret back to 45% of the page', () => {
    expect(caretScrollDelta(900, 1000)).toBe(450);
    expect(caretScrollDelta(100, 1000)).toBe(-350);
  });

  it('ignores invalid viewports', () => {
    expect(caretScrollDelta(100, 0)).toBeNull();
    expect(caretScrollDelta(Number.NaN, 1000)).toBeNull();
  });
});

describe('countWords', () => {
  it('counts words in any script and ignores punctuation', () => {
    expect(countWords('Hello, world — again!')).toBe(3);
    expect(countWords('Søren’s Either/Or')).toBe(3);
    expect(countWords('')).toBe(0);
  });
});

describe('looksLikeMarkdown', () => {
  it('recognises Markdown that should be pasted as structure', () => {
    expect(looksLikeMarkdown('# Heading\n\nText')).toBe(true);
    expect(looksLikeMarkdown('- one\n- two')).toBe(true);
    expect(looksLikeMarkdown('Some **bold** words')).toBe(true);
    expect(looksLikeMarkdown('a [link](https://x.y)')).toBe(true);
  });

  it('leaves ordinary prose alone', () => {
    expect(looksLikeMarkdown('Just a sentence.')).toBe(false);
    expect(looksLikeMarkdown('Two lines\nof prose.')).toBe(false);
  });
});
