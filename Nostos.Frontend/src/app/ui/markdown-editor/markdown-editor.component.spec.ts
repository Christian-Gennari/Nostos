import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  MarkdownEditorComponent,
  caretScrollDelta,
  countWords,
  looksLikeMarkdown,
} from './markdown-editor.component';
import { TypewriterSounds } from './typewriter-sounds';

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

  it('has no toolbar: formatting lives in the selection bubble, with no underline', async () => {
    await create('Text.');
    expect(fixture.nativeElement.querySelector('.editor-toolbar')).toBeNull();
    // The bubble is detached until a selection exists; its buttons are still there.
    const bubble = (component as unknown as { bubble: { nativeElement: HTMLElement } }).bubble.nativeElement;
    const labels = [...bubble.querySelectorAll('[aria-label]')].map((el) => el.getAttribute('aria-label'));
    expect(labels).not.toContain('Underline');
    expect(labels).toEqual(expect.arrayContaining(['Bold', 'Italic', 'Heading 2', 'Heading 3', 'Quote', 'Link']));
  });

  it('toggles a block style off when it is applied again', async () => {
    await create('A line');
    component.toggleBlock('h2');
    expect(component.currentMarkdown()).toBe('## A line');
    component.toggleBlock('h2');
    expect(component.currentMarkdown()).toBe('A line');
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

    it('opens with the shortcut and takes its @ (and its space) back when closed empty', async () => {
      await create('Word');
      (component as any).editor.commands.focus('end');
      expect(component.openSourceQuery()).toBe(true);
      expect(component.currentMarkdown()).toBe('Word @');
      component.closeSourceQuery();
      await Promise.resolve();
      expect(component.currentMarkdown()).toBe('Word');
    });

    it('never replaces selected text with the shortcut', async () => {
      await create('Keep this sentence.');
      (component as any).editor.commands.selectAll();
      component.openSourceQuery();
      expect(component.currentMarkdown()).toContain('Keep this sentence.');
      expect(component.currentMarkdown()).toContain('@');
    });

    it('adds no space after opening punctuation', async () => {
      await create('(');
      (component as any).editor.commands.focus('end');
      component.openSourceQuery();
      expect(component.currentMarkdown()).toBe('(@');
    });

    it('puts a block after a selected image instead of failing', async () => {
      await create('![dot](https://example.com/a.png)\n\nAfter.');
      const editor = (component as any).editor as import('@tiptap/core').Editor;
      let imagePos = -1;
      editor.state.doc.descendants((node, pos) => {
        if (node.type.name === 'image' && imagePos < 0) imagePos = pos;
      });
      editor.commands.setNodeSelection(imagePos);
      expect(component.insertBlockMarkdown('> Quoted.')).toBe(true);
      expect(component.currentMarkdown()).toMatch(/a\.png\)\n\n> Quoted\./);
    });
  });

  it('keeps spell checking off through a redraw once the setting is off', async () => {
    await create('Text.');
    expect(page().getAttribute('spellcheck')).toBe('true');
    fixture.componentRef.setInput('spellcheck', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(page().getAttribute('spellcheck')).toBe('false');
    await component.insertMarkdown('More.');
    expect(page().getAttribute('spellcheck')).toBe('false');
  });

  it('fetches no sound samples while typewriter sounds are off', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    await create('Text.');
    page().dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertText', data: 'a', bubbles: true }));
    expect(fetchSpy.mock.calls.filter(([u]) => String(u).includes('/audio/typewriter/'))).toEqual([]);
    fetchSpy.mockRestore();
  });

  it('rings the margin bell on the character that reaches the margin, once per line (#1065)', async () => {
    const played: string[] = [];
    const play = vi.spyOn(TypewriterSounds.prototype, 'play').mockImplementation((sound) => void played.push(sound));
    vi.spyOn(TypewriterSounds.prototype, 'prefetch').mockResolvedValue(undefined);
    await create('x'.repeat(61));
    fixture.componentRef.setInput('sounds', true);
    fixture.detectChanges();
    const editor = (component as any).editor as import('@tiptap/core').Editor;
    const type = (ch: string) => {
      page().dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertText', data: ch, bubbles: true }));
      editor.commands.insertContent(ch);
    };
    editor.commands.focus('end');
    type('a'); // column 61: not yet
    expect(played).toEqual(['key']);
    type('b'); // column 62: eight before the 70-column margin
    expect(played).toEqual(['key', 'key', 'bell']);
    type('c');
    expect(played.filter((s) => s === 'bell').length).toBe(1);

    // A soft keyboard committing a word across the column rings too.
    played.length = 0;
    editor.commands.insertContent(' ');
    editor.commands.insertContent('x'.repeat(2 * 70 - 8 - 66));
    page().dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertText', data: 'words', bubbles: true }));
    expect(played).toEqual(['key', 'bell']);

    played.length = 0;
    fixture.componentRef.setInput('soundPack', 'keyboard');
    fixture.detectChanges();
    editor.commands.setTextSelection(62 + 1); // back at column 62
    type('d');
    expect(played).toEqual(['key']);
    play.mockRestore();
  });

  describe('the / menu (#1043)', () => {
    const typeText = (text: string) => {
      const editor = (component as unknown as { editor: import('@tiptap/core').Editor }).editor;
      for (const ch of text) editor.commands.insertContent(ch);
    };

    it('opens on an empty line and turns "/h2" into a heading', async () => {
      await create('Intro.');
      (component as any).editor.commands.focus('end');
      (component as any).editor.commands.enter();
      typeText('/h2');
      expect(component.blockMenu()?.items[0].action).toBe('h2');
      component.runBlockAction('h2');
      typeText('Section');
      expect(component.currentMarkdown()).toBe('Intro.\n\n## Section');
      expect(component.blockMenu()).toBeNull();
    });

    it('does not open in the middle of a sentence', async () => {
      await create('and');
      (component as any).editor.commands.focus('end');
      typeText('/or');
      expect(component.blockMenu()).toBeNull();
    });

    it('inserts a divider and a table that save as Markdown', async () => {
      await create('');
      (component as any).editor.commands.focus('end');
      typeText('/div');
      component.runBlockAction('divider');
      expect(component.currentMarkdown()).toContain('* * *');
      (component as any).editor.commands.focus('end');
      typeText('/table');
      component.runBlockAction('table');
      expect(component.currentMarkdown()).toMatch(/\|\s*\|\s*\|/);
    });

    it('asks for an address for an image, then inserts it', async () => {
      await create('');
      (component as any).editor.commands.focus('end');
      typeText('/img');
      component.runBlockAction('image');
      expect(component.blockMenuImage()).toBe(true);
      component.blockMenuImageUrl.set('https://example.com/a.png');
      component.applyBlockImage();
      expect(component.currentMarkdown()).toBe('![](https://example.com/a.png)');
    });

    it('keeps the image target on the "/img" line when text before it changes', async () => {
      await create('First.');
      const editor = (component as any).editor as import('@tiptap/core').Editor;
      editor.commands.focus('end');
      editor.commands.enter();
      typeText('/img');
      component.runBlockAction('image');
      // Text added above while the address field is up shifts every position.
      editor.commands.insertContentAt(1, 'Very ');
      component.blockMenuImageUrl.set('https://example.com/a.png');
      component.applyBlockImage();
      expect(component.currentMarkdown()).toBe('Very First.\n\n![](https://example.com/a.png)');
    });

    it('drops the image step when the writer goes back to the page', async () => {
      await create('');
      const editor = (component as any).editor as import('@tiptap/core').Editor;
      editor.commands.focus('end');
      typeText('/img');
      component.runBlockAction('image');
      expect(component.blockMenuImage()).toBe(true);
      editor.emit('focus', { editor, event: new FocusEvent('focus'), transaction: editor.state.tr });
      expect(component.blockMenuImage()).toBe(false);
      expect(component.blockMenu()).toBeNull();
    });

    it('does not open inside a list item', async () => {
      await create('- one');
      const editor = (component as any).editor as import('@tiptap/core').Editor;
      editor.commands.focus('end');
      editor.commands.enter();
      typeText('/bul');
      expect(component.blockMenu()).toBeNull();
    });

    it('does not open inside a table cell', async () => {
      await create('| a | b |\n| --- | --- |\n|  |  |');
      const cellEditor = (component as any).editor as import('@tiptap/core').Editor;
      let cellPos = -1;
      cellEditor.state.doc.descendants((node, pos) => {
        if (node.type.name === 'tableCell' && cellPos < 0) cellPos = pos + 2;
      });
      cellEditor.commands.focus(cellPos);
      typeText('/table');
      expect(component.blockMenu()).toBeNull();
    });

    it('goes back to the best match when the query changes', async () => {
      await create('');
      (component as any).editor.commands.focus('end');
      typeText('/h');
      component.blockMenuActive.set(2);
      typeText('e');
      expect(component.blockMenuActive()).toBe(0);
    });

    it('opens above the line when the page has no room below', async () => {
      await create('');
      component.blockMenu.set({ items: [], rect: { top: window.innerHeight - 30, bottom: window.innerHeight - 10, left: 40, right: 60 } });
      const placement = component.blockMenuPlacement()!;
      expect(placement.top).toBeNull();
      expect(placement.bottom).toBe(36);
    });

    it('hands over to the @ palette for a source', async () => {
      await create('');
      const states: unknown[] = [];
      component.sourceSuggestion.subscribe((st) => states.push(st));
      (component as any).editor.commands.focus('end');
      typeText('/source');
      component.runBlockAction('source');
      expect(states.at(-1)).toEqual(expect.objectContaining({ query: '' }));
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
