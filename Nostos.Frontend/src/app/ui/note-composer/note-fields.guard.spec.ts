/**
 * Every place a note is written uses the note composer (issue #1449).
 *
 * Topics once worked one way on the Brain's cards (pills) and another in the
 * reader's selection note (brackets typed from memory), because each note field
 * was built by hand. The composer is the one note field: the words with `[[`
 * autocomplete, and the topics as pills under them (docs/topic-system.md,
 * "Writing a note"). This guard keeps a new note field from skipping it:
 *
 * - `app-topic-input` (the words alone) is used only inside the composer;
 * - every other `<textarea>` in the app is listed below with what it holds.
 *   A new one fails here until it is either a note field (use
 *   `app-note-composer`) or listed with the reason it is not one.
 */
// vitest resolves the node built-ins at runtime.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

import { describe, expect, it } from 'vitest';

/** Vitest runs with the project root as cwd (Nostos.Frontend). */
const APP = path.resolve(process.cwd(), 'src/app');

/** Textareas that are not a note's words, by file, with what they hold. */
const NOT_NOTE_FIELDS: Record<string, string> = {
  'ui/topic-input.component/topic-input.component.html': 'the composer’s own words field',
  'ui/note-item/note-item.component.html': 'the quote of a highlight (its selected text), not the note',
  'book-detail/photo-quote/photo-quote-modal.component.html': 'the quote read from a photo, not the note',
  'reading-list/reading-list.component.html': 'why a book is on the reading list',
  'brain/brain-topic-words.component.html': 'a topic’s own description',
  'ui/assistant/assistant.component.html': 'the Ask Nostos chat box',
  'add-book-modal/add-book-modal.component.html': 'a book’s personal review',
  'ui/ui-catalogue/ui-catalogue.component.html': 'the catalogue’s form control samples',
};

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return files(full);
    return /\.(html|ts)$/.test(name) && !name.endsWith('.spec.ts') ? [full] : [];
  });
}

const sources = files(APP).map((file) => ({
  file: path.relative(APP, file).split(path.sep).join('/'),
  text: readFileSync(file, 'utf8'),
}));

describe('note fields (issue #1449)', () => {
  it('uses the bare words field only inside the note composer', () => {
    const users = sources
      .filter(({ text }) => text.includes('<app-topic-input'))
      .map(({ file }) => file);

    expect(users).toEqual(['ui/note-composer/note-composer.component.ts']);
  });

  it('has no textarea that is not either the composer or listed with what it holds', () => {
    const unlisted = sources
      .filter(({ text }) => /<textarea\b/.test(text))
      .map(({ file }) => file)
      .filter((file) => !(file in NOT_NOTE_FIELDS));

    expect(unlisted).toEqual([]);
  });

  it('lists only files that still have a textarea', () => {
    const stale = Object.keys(NOT_NOTE_FIELDS).filter(
      (file) => !sources.some((source) => source.file === file && /<textarea\b/.test(source.text)),
    );

    expect(stale).toEqual([]);
  });
});
