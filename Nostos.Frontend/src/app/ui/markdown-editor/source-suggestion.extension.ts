import { Extension, Range } from '@tiptap/core';
import { PluginKey } from '@tiptap/pm/state';
import Suggestion, { SuggestionKeyDownProps, SuggestionProps } from '@tiptap/suggestion';

/**
 * The `@` trigger for the Studio's source palette (issue #1039). It only
 * reports what is typed after `@` and where; what the palette finds and
 * inserts belongs to the Studio.
 */
export const SOURCE_SUGGESTION_KEY = new PluginKey('nostosSourceSuggestion');

/**
 * `@` opens the palette at the start of a word: after a space or line start,
 * or after opening punctuation. Mid-word (an email address) it does not.
 */
export const SOURCE_SUGGESTION_PREFIXES = [' ', ' ', '(', '[', '"', '“', '‘', "'", '—', '–'];

/** The longest query the palette follows before it is plainly just prose. */
const MAX_QUERY_LENGTH = 80;
const MAX_QUERY_WORDS = 8;

export interface SourceSuggestionHandlers {
  onChange: (state: { query: string; range: Range; rect: DOMRect | null } | null) => void;
  onKeyDown: (event: KeyboardEvent) => boolean;
  onOpenShortcut: () => boolean;
}

/** Whether text typed after `@` still reads as a palette query. */
export function isPaletteQuery(query: string): boolean {
  if (query.length > MAX_QUERY_LENGTH) return false;
  if (/^\s/.test(query) || /\s{2,}/.test(query)) return false;
  return query.trim().split(/\s+/).filter(Boolean).length <= MAX_QUERY_WORDS;
}

export const SourceSuggestion = Extension.create<SourceSuggestionHandlers>({
  name: 'nostosSourceSuggestion',

  addOptions() {
    return {
      onChange: () => undefined,
      onKeyDown: () => false,
      onOpenShortcut: () => false,
    };
  },

  addKeyboardShortcuts() {
    // Ctrl/Cmd+J belongs to the assistant; ' reads as "quote".
    return { "Mod-'": () => this.options.onOpenShortcut() };
  },

  addProseMirrorPlugins() {
    const options = this.options;
    return [
      Suggestion({
        editor: this.editor,
        pluginKey: SOURCE_SUGGESTION_KEY,
        char: '@',
        allowSpaces: true,
        allowedPrefixes: SOURCE_SUGGESTION_PREFIXES,
        allow: ({ state, range }) => {
          const $from = state.doc.resolve(range.from);
          if ($from.parent.type.spec.code) return false;
          return isPaletteQuery(state.doc.textBetween(range.from + 1, range.to, ' ', ' '));
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
