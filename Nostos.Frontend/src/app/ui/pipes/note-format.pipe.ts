import { Pipe, PipeTransform } from '@angular/core';
import { TopicDto } from '../../core/services/topics.service';

/**
 * Apply the app's wikilink grammar with a caller-chosen rendering. Rich notes
 * and text-only previews share this parser so their displayed labels agree.
 */
export function replaceNoteWikilinks(
  content: string,
  render: (topicName: string, displayText: string) => string,
): string {
  return content.replace(/\[\[(.*?)\]\]/g, (_match, rawLink: string) => {
    const separator = rawLink.indexOf('|');
    const topicName = (separator < 0 ? rawLink : rawLink.slice(0, separator)).trim();
    const alias = separator < 0 ? '' : rawLink.slice(separator + 1).trim();
    return render(topicName, alias || topicName);
  });
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
  transform(content: string, topicMap: Map<string, TopicDto> | null): string {
    if (!content) return '';

    const topics = topicMap ?? new Map<string, TopicDto>();

    return replaceNoteWikilinks(content, (topicName, displayText) => {
      const escapedName = this.escapeHtml(displayText);
      const topic = topics.get(topicName.toLocaleLowerCase());

      if (topic) {
        const href = `/second-brain?topicId=${encodeURIComponent(topic.id)}`;
        return `<a class="topic-tag clickable" href="${href}" data-topic-id="${topic.id}" aria-label="${escapedName} — open topic evidence in Brain">${escapedName}</a>`;
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
