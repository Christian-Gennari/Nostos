import { readFileSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import { removeTopic, splitTopics, topicNames } from './topic-text';

/**
 * The cases the server's TopicReferences runs too
 * (Nostos.Backend.Tests/Services/TopicReferenceCasesTests.cs).
 */
interface Cases {
  compose: { words: string; topics: string[]; content: string }[];
  split: { content: string; words: string; topics: string[] }[];
  remove: { content: string; name: string; expected: string }[];
}

const cases: Cases = JSON.parse(
  readFileSync(path.resolve(process.cwd(), '../Nostos.Shared/Fixtures/topic-references.json'), 'utf8'),
);

describe('topic text rules (shared with the server)', () => {
  it.each(cases.split)('splits $content into words and pill topics', ({ content, words, topics }) => {
    expect(splitTopics(content)).toEqual({ words, topics });
  });

  it.each(cases.remove)('removes $name from $content', ({ content, name, expected }) => {
    expect(removeTopic(content, name)).toBe(expected);
  });

  it.each(cases.compose)('reads back what the server writes for $words + $topics', ({ words, topics, content }) => {
    const split = splitTopics(content);
    const inline = topicNames(words).map((name) => name.toLocaleLowerCase());
    const expectedPills = topics.filter(
      (name, i) =>
        !inline.includes(name.toLocaleLowerCase())
        && topics.findIndex((other) => other.toLocaleLowerCase() === name.toLocaleLowerCase()) === i,
    );
    expect(split.words).toBe(words.trim());
    expect(split.topics).toEqual(expectedPills);
  });

  it('drops a pill line it empties, with the blank line before it', () => {
    expect(removeTopic('A thought.\n\n[[Faith]]', 'faith')).toBe('A thought.');
    expect(removeTopic('A thought.\n\n[[a]], [[b]]', 'a')).toBe('A thought.\n\n[[b]]');
  });
});
