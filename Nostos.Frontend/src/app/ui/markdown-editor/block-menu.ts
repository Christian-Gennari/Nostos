import { Extension, Range } from '@tiptap/core';
import { PluginKey } from '@tiptap/pm/state';
import Suggestion, { SuggestionKeyDownProps, SuggestionProps } from '@tiptap/suggestion';

/**
 * The `/` menu (issue #1043): with the toolbar gone, the rarer blocks (a
 * divider, an image, a table, code) need a home the keyboard can reach. It
 * opens only on an empty line, so "and/or" and dates never trigger it.
 */
export type BlockMenuAction =
  | 'h1'
  | 'h2'
  | 'h3'
  | 'blockquote'
  | 'bulletList'
  | 'orderedList'
  | 'divider'
  | 'codeBlock'
  | 'image'
  | 'table'
  | 'source';

export interface BlockMenuItem {
  action: BlockMenuAction;
  label: string;
  /** The Markdown shortcut that does the same, so the menu teaches it. */
  hint: string;
  keywords: string;
}

export const BLOCK_MENU_ITEMS: readonly BlockMenuItem[] = [
  { action: 'h1', label: 'Heading 1', hint: '# ', keywords: 'h1 title heading' },
  { action: 'h2', label: 'Heading 2', hint: '## ', keywords: 'h2 heading section' },
  { action: 'h3', label: 'Heading 3', hint: '### ', keywords: 'h3 heading subsection' },
  { action: 'blockquote', label: 'Quote', hint: '> ', keywords: 'quote blockquote citation' },
  { action: 'bulletList', label: 'Bulleted list', hint: '- ', keywords: 'list bullets ul' },
  { action: 'orderedList', label: 'Numbered list', hint: '1. ', keywords: 'list numbers ol ordered' },
  { action: 'divider', label: 'Divider', hint: '---', keywords: 'divider rule hr line break separator' },
  { action: 'codeBlock', label: 'Code block', hint: '```', keywords: 'code pre fenced' },
  { action: 'image', label: 'Image', hint: '![](url)', keywords: 'image picture photo img' },
  { action: 'table', label: 'Table', hint: '| a | b |', keywords: 'table grid columns' },
  { action: 'source', label: 'Insert source…', hint: '@', keywords: 'source quote note passage reference cite' },
];

/** Items whose label or keywords hold every letter of the query, in order. */
export function filterBlockMenu(query: string): BlockMenuItem[] {
  const q = query.trim().toLowerCase();
  if (!q) return [...BLOCK_MENU_ITEMS];
  const subsequence = (text: string) => {
    let i = 0;
    for (const ch of text) if (ch === q[i]) i++;
    return i === q.length;
  };
  const starts = (item: BlockMenuItem) =>
    item.label.toLowerCase().startsWith(q) || item.keywords.split(' ').some((k) => k.startsWith(q));
  return BLOCK_MENU_ITEMS.filter((item) => starts(item) || subsequence(item.label.toLowerCase()))
    .sort((a, b) => Number(starts(b)) - Number(starts(a)));
}

export const BLOCK_MENU_KEY = new PluginKey('nostosBlockMenu');

export interface BlockMenuHandlers {
  onChange: (state: { query: string; range: Range; rect: DOMRect | null } | null) => void;
  onKeyDown: (event: KeyboardEvent) => boolean;
}

export const BlockMenu = Extension.create<BlockMenuHandlers>({
  name: 'nostosBlockMenu',
  // Ahead of StarterKit's keymaps, like the @ palette: Enter picks an item.
  priority: 1000,

  addOptions() {
    return { onChange: () => undefined, onKeyDown: () => false };
  },

  addProseMirrorPlugins() {
    const options = this.options;
    return [
      Suggestion({
        editor: this.editor,
        pluginKey: BLOCK_MENU_KEY,
        char: '/',
        startOfLine: true,
        allowSpaces: false,
        allowedPrefixes: null,
        // Only a paragraph holding nothing but the "/query" itself.
        allow: ({ state, range }) => {
          const $from = state.doc.resolve(range.from);
          const parent = $from.parent;
          if (parent.type.name !== 'paragraph') return false;
          return parent.textContent === state.doc.textBetween(range.from, range.to);
        },
        render: () => {
          const report = (props: SuggestionProps) =>
            options.onChange({ query: props.query, range: props.range, rect: props.clientRect?.() ?? null });
          return {
            onStart: report,
            onUpdate: report,
            onExit: () => options.onChange(null),
            onKeyDown: ({ event }: SuggestionKeyDownProps) => options.onKeyDown(event),
          };
        },
      }),
    ];
  },
});
