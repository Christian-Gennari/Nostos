import { NoteFormatPipe, noteWikilinksToText } from './note-format.pipe';
import { TopicDto } from '../../core/services/topics.service';

describe('NoteFormatPipe', () => {
  const pipe = new NoteFormatPipe();
  const topic: TopicDto = { id: 'topic-1', name: 'Freedom', usageCount: 2 };
  const map = new Map<string, TopicDto>([['freedom', topic]]);

  it('renders a resolved wikilink as an accessible Brain link', () => {
    const html = pipe.transform('Thinking about [[Freedom]].', map);

    expect(html).toContain('class="topic-tag clickable"');
    expect(html).toContain('href="/brain/topics/topic-1"');
    expect(html).toContain('data-topic-id="topic-1"');
    expect(html).toContain('open the topic in Brain');
    expect(html).toContain('>Freedom</a>');
  });

  it('leaves text that is not a valid reference as plain text', () => {
    const text = 'open [[ no close and [[]] empty and [[a|b]] pipe and ]] stray';
    const html = pipe.transform(text, map);

    expect(html).not.toContain('topic-tag');
    expect(html).toBe(text);
  });

  it('reads the valid reference after an unclosed opener', () => {
    const html = pipe.transform('x [[ open and [[Freedom]] done', map);

    expect(html).toContain('x [[ open and <a');
    expect(html).toContain('>Freedom</a>');
  });

  it('shows wikilinks in text-only previews the same way', () => {
    expect(noteWikilinksToText('[[Freedom]] and [[a|b]] and [[]] and [[ open')).toBe(
      'Freedom and [[a|b]] and [[]] and [[ open',
    );
  });

  it('keeps an unresolved wikilink visibly unresolved', () => {
    const html = pipe.transform('Thinking about [[Missing idea]].', map);

    expect(html).toContain('topic-tag--unresolved');
    expect(html).toContain('Missing idea');
    expect(html).toContain('unresolved');
    expect(html).toContain('Topic not found');
  });

  it('shows unresolved state even when the topic index is empty', () => {
    const html = pipe.transform('[[Unindexed]]', new Map());

    expect(html).toContain('topic-tag--unresolved');
    expect(html).not.toBe('[[Unindexed]]');
  });
});
