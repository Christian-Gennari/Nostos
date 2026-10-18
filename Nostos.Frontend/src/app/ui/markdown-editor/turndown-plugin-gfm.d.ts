// turndown-plugin-gfm ships no types. It exports Turndown plugins: each is a
// function that registers rules on the service it is given.
declare module 'turndown-plugin-gfm' {
  import type TurndownService from 'turndown';
  type Plugin = (service: TurndownService) => void;
  export const gfm: Plugin;
  export const tables: Plugin;
  export const strikethrough: Plugin;
  export const taskListItems: Plugin;
  export const highlightedCodeBlock: Plugin;
}
