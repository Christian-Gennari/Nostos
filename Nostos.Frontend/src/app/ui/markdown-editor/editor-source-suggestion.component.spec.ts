import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';

import { EditorSourceSuggestionComponent, SourceSuggestionState } from './editor-source-suggestion.component';
import { SourceSuggestion } from './source-suggestion.extension';

describe('EditorSourceSuggestionComponent', () => {
  let fixture: ComponentFixture<EditorSourceSuggestionComponent>;
  let component: EditorSourceSuggestionComponent;
  let editor: Editor;
  let hostEl: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EditorSourceSuggestionComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(EditorSourceSuggestionComponent);
    component = fixture.componentInstance;
    hostEl = document.createElement('div');
    document.body.appendChild(hostEl);

    editor = new Editor({
      element: hostEl,
      extensions: [
        StarterKit,
        SourceSuggestion.configure(component.extensionOptions()),
      ],
      content: '<p>Hello</p>',
    });

    component.attachEditor(editor);
    fixture.componentRef.setInput('editor', editor);
    fixture.componentRef.setInput('editorDom', editor.view.dom as HTMLElement);
    fixture.detectChanges();
  });

  afterEach(() => {
    editor?.destroy();
    hostEl?.remove();
    fixture?.destroy();
  });

  it('syncs combobox ARIA attributes onto editorDom when activeId changes', () => {
    const dom = editor.view.dom as HTMLElement;
    expect(dom.getAttribute('aria-expanded')).toBe('false');
    expect(dom.hasAttribute('aria-activedescendant')).toBe(false);

    fixture.componentRef.setInput('activeId', 'source-opt-2');
    fixture.detectChanges();

    expect(dom.getAttribute('aria-activedescendant')).toBe('source-opt-2');
    expect(dom.getAttribute('aria-controls')).toBe('source-palette-list');
    expect(dom.getAttribute('aria-expanded')).toBe('true');

    fixture.componentRef.setInput('activeId', null);
    fixture.detectChanges();

    expect(dom.hasAttribute('aria-activedescendant')).toBe(false);
    expect(dom.hasAttribute('aria-controls')).toBe(false);
    expect(dom.getAttribute('aria-expanded')).toBe('false');
  });

  it('opens with shortcut, reports open state, and cleans up empty @ on close', async () => {
    const states: (SourceSuggestionState | null)[] = [];
    const opens: boolean[] = [];
    component.suggestionChange.subscribe((s) => states.push(s));
    component.openChange.subscribe((open) => opens.push(open));

    editor.commands.focus('end');
    expect(component.openQuery()).toBe(true);
    expect(component.isOpen()).toBe(true);
    expect(opens.at(-1)).toBe(true);
    expect(states.at(-1)).toEqual(expect.objectContaining({ query: '' }));
    expect(editor.getText()).toBe('Hello @');

    component.closeQuery();
    await Promise.resolve();

    expect(component.isOpen()).toBe(false);
    expect(opens.at(-1)).toBe(false);
    expect(states.at(-1)).toBeNull();
    expect(editor.getText()).toBe('Hello');
  });

  it('forwards keyDownHandler while a query is open', () => {
    const handler = vi.fn(() => true);
    fixture.componentRef.setInput('keyDownHandler', handler);
    fixture.detectChanges();

    editor.commands.focus('end');
    component.openQuery();

    const event = new KeyboardEvent('keydown', { key: 'ArrowDown', cancelable: true });
    const handled = component.extensionOptions().onKeyDown(event);
    expect(handled).toBe(true);
    expect(handler).toHaveBeenCalledWith(event);
  });
});
