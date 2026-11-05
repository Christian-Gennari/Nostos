import { Pipe, PipeTransform } from '@angular/core';
import { replaceTopicReferences } from '../note-item/note-item.helpers';
import { TopicDto } from '../../core/services/topics.service';

/**
 * Apply the app's wikilink grammar with a caller-chosen rendering. Rich notes
 * and text-only previews share this parser so their displayed labels agree.
 * Only `[[Name]]` with a valid topic name is a link; there is no alias syntax,
 * so `[[a|b]]` and an unclosed `[[` stay plain text.
 */
export function replaceNoteWikilinks(
  content: string,
  render: (topicName: string, displayText: string) => string,
): string {
  return replaceTopicReferences(content, (name) => render(name, name));
}

/** Replace wikilinks with the text a reader sees in the rendered note. */
export function noteWikilinksToText(content: string): string {
  return replaceNoteWikilinks(content, (_topicName, displayText) => displayText);
}

@Pipe({
  name: 'noteFormat',
  standalone: true,
})
export class NoteFormatPipe implements PipeTransform {
  transform(content: string, topicMap: ReadonlyMap<string, TopicDto> | null): string {
    if (!content) return '';

    const topics = topicMap ?? new Map<string, TopicDto>();

    // The note is shown as typed: anything that is not a wikilink is text, so
    // HTML in it is escaped rather than handed to the sanitiser to render.
    let html = '';
    let last = 0;
    for (const match of content.matchAll(/\[\[(.*?)\]\]/g)) {
      html += this.escapeHtml(content.slice(last, match.index));
      html += this.renderLink(match[1], topics);
      last = match.index + match[0].length;
    }
    return html + this.escapeHtml(content.slice(last));
  }

  private renderLink(rawLink: string, topics: ReadonlyMap<string, TopicDto>): string {
    return replaceNoteWikilinks(`[[${rawLink}]]`, (topicName, displayText) => {
      const escapedName = this.escapeHtml(displayText);
      const topic = topics.get(topicName.toLocaleLowerCase());

      if (topic) {
        const href = `/brain/topics/${encodeURIComponent(topic.id)}`;
        return `<a class="topic-tag clickable" href="${href}" data-topic-id="${topic.id}" aria-label="${escapedName} — open the topic in Brain">${escapedName}</a>`;
      }

      // Keep the link identity visible even when its backing topic is missing.
      // Silent plain text made a broken relationship look intentional.
      return `<span class="topic-tag topic-tag--unresolved" aria-label="Unresolved topic: ${escapedName}" title="Topic not found">${escapedName}<span class="topic-unresolved-label"> · unresolved</span></span>`;
    });
  }

  private escapeHtml(value: string): string {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }
}
