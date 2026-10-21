import { Pipe, PipeTransform } from '@angular/core';
import { Marked } from 'marked';

/**
 * Escape raw HTML before it reaches Angular's HTML binding. Angular still
 * sanitizes the final rendered string; this renderer additionally makes model
 * supplied HTML visible as text instead of treating it as part of the Markdown
 * surface at all.
 */
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/**
 * Ask Nostos has its own Marked instance. The Writing Studio also uses Marked,
 * and sharing the global instance would let one surface's renderer extensions
 * silently change the other.
 */
const assistantMarkdown = new Marked();

/**
 * The previews for the reply being rendered, by citation number. Set for the
 * length of one synchronous parse; Marked renderers take no per-call options.
 */
let citationPreviews: readonly string[] = [];

/**
 * `[^n]` is a numbered citation (issue #1002): the server keeps a marker only
 * when it points at the turn's evidence n. It renders as a small superscript
 * link, previewed on hover through its title and opened by the transcript's
 * click handler. Markers in code stay code.
 */
assistantMarkdown.use({
  extensions: [
    {
      name: 'citation',
      level: 'inline',
      start(src: string) {
        const index = src.indexOf('[^');
        return index < 0 ? undefined : index;
      },
      tokenizer(src: string) {
        const match = /^\[\^(\d{1,3})\]/.exec(src);
        return match ? { type: 'citation', raw: match[0], number: Number(match[1]) } : undefined;
      },
      renderer(token) {
        const number = Number(token['number']);
        const preview = citationPreviews[number - 1];
        const title = preview ? ` title="${escapeHtml(preview)}"` : '';
        return `<sup class="assistant-cite"><a class="assistant-cite-link" href="#source-${number}"${title} aria-label="Source ${number}">${number}</a></sup>`;
      },
    },
  ],
});

assistantMarkdown.use({
  gfm: true,
  breaks: true,
  silent: true,
  renderer: {
    html({ text }) {
      return escapeHtml(text);
    },

    image({ text }) {
      const label = text.trim();
      const copy = label ? `Image omitted: ${label}` : 'Image omitted';
      return `<span class="assistant-markdown-image-omitted" role="note">[${escapeHtml(copy)}]</span>`;
    },

    checkbox({ checked }) {
      return `<span class="assistant-markdown-task-marker" aria-hidden="true">${checked ? '☑' : '☐'}</span> `;
    },
  },
});

/**
 * Compile one assistant reply to safe-to-bind HTML.
 *
 * This deliberately returns an ordinary string rather than SafeHtml. Binding
 * the string through Angular's [innerHTML] keeps Angular's sanitizer in the
 * path for links and every other generated attribute.
 */
export function renderAssistantMarkdown(source: string, previews: readonly string[] = []): string {
  citationPreviews = previews;
  try {
    const rendered = assistantMarkdown.parse(source);
    return typeof rendered === 'string' ? rendered : escapeHtml(source);
  } finally {
    citationPreviews = [];
  }
}

@Pipe({
  name: 'assistantMarkdown',
  standalone: true,
  pure: true,
})
export class AssistantMarkdownPipe implements PipeTransform {
  transform(source: string, previews: readonly string[] = []): string {
    return renderAssistantMarkdown(source, previews);
  }
}
