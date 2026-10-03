import { Pipe, PipeTransform } from '@angular/core';
import { TopicDto } from '../../core/services/topics.service';

@Pipe({
  name: 'noteFormat',
  standalone: true,
})
export class NoteFormatPipe implements PipeTransform {
  transform(content: string, topicMap: Map<string, TopicDto> | null): string {
    if (!content) return '';

    const topics = topicMap ?? new Map<string, TopicDto>();

    return content.replace(/\[\[(.*?)\]\]/g, (_match, topicName: string) => {
      const trimmedName = topicName.trim();
      const escapedName = this.escapeHtml(trimmedName);
      const topic = topics.get(trimmedName.toLocaleLowerCase());

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
