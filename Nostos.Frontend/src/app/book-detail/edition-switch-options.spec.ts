import type { Book, EditionSummaryDto } from '../core/dtos/book.dtos';
import { buildEditionSwitchOptions, editionChoices } from './edition-switch-options';

function book(overrides: Partial<Book> = {}): Book {
  return {
    id: 'b1',
    type: 'ebook',
    title: 'Dune',
    fileName: 'dune.epub',
    edition: null,
    publisher: null,
    publishedDate: null,
    narrator: null,
    duration: null,
    progressPercent: 0,
    finishedAt: null,
    otherEditions: [],
    ...overrides,
  } as Book;
}

function edition(overrides: Partial<EditionSummaryDto> & { id: string }): EditionSummaryDto {
  return { type: 'ebook', progressPercent: 0, hasFile: true, ...overrides };
}

const physical = (overrides: Partial<Book> = {}) => book({ type: 'physical', fileName: null, ...overrides });

describe('buildEditionSwitchOptions', () => {
  it('is empty for a single-edition book', () => {
    expect(buildEditionSwitchOptions(book())).toEqual([]);
    expect(buildEditionSwitchOptions(null)).toEqual([]);
  });

  it('orders by format and gives a unique format no detail', () => {
    const options = buildEditionSwitchOptions(
      book({
        otherEditions: [
          edition({ id: 'b3', type: 'physical' }),
          edition({ id: 'b2', type: 'audiobook', narrator: 'Scott Brick' }),
          edition({ id: 'b4', fileName: 'dune.pdf' }),
        ],
      }),
    );

    expect(options.map((o) => [o.format, o.concept, o.detail, o.current])).toEqual([
      ['Audiobook', 'audiobook', null, false],
      ['Ebook', 'ebook', null, true],
      ['PDF', 'pdf', null, false],
      ['Physical', 'physical', null, false],
    ]);
  });

  it('holds the same order whichever edition is current', () => {
    const fromEpub = buildEditionSwitchOptions(book({ otherEditions: [edition({ id: 'b2', type: 'audiobook' })] }));
    const fromAudio = buildEditionSwitchOptions(
      book({ id: 'b2', type: 'audiobook', fileName: null, otherEditions: [edition({ id: 'b1', fileName: 'dune.epub' })] }),
    );

    expect(fromEpub.map((o) => o.id)).toEqual(fromAudio.map((o) => o.id));
  });

  it('names same-format editions by edition first, adding the noun', () => {
    const options = buildEditionSwitchOptions(
      physical({ edition: '2nd', otherEditions: [edition({ id: 'b2', type: 'physical', edition: 'Revised edition' })] }),
    );

    expect(options.map((o) => o.detail)).toEqual(['2nd edition', 'Revised edition']);
  });

  it('falls back to the year when the editions share no edition detail', () => {
    const options = buildEditionSwitchOptions(
      physical({
        publishedDate: '1965-08-01',
        publisher: 'Chilton',
        otherEditions: [edition({ id: 'b2', type: 'physical', publishedDate: '1990', publisher: 'Ace' })],
      }),
    );

    expect(options.map((o) => o.detail)).toEqual(['1965', '1990']);
  });

  it('names two audiobooks by narrator', () => {
    const options = buildEditionSwitchOptions(
      book({
        type: 'audiobook',
        fileName: null,
        narrator: 'Scott Brick',
        otherEditions: [edition({ id: 'b2', type: 'audiobook', narrator: 'Simon Vance' })],
      }),
    );

    expect(options.map((o) => o.detail)).toEqual(['Read by Scott Brick', 'Read by Simon Vance']);
  });

  it('uses the publisher when it is the only difference', () => {
    const options = buildEditionSwitchOptions(
      physical({ publisher: 'Chilton', otherEditions: [edition({ id: 'b2', type: 'physical', publisher: 'Ace' })] }),
    );

    expect(options.map((o) => o.detail)).toEqual(['Chilton', 'Ace']);
  });

  it('gives each edition its own detail when no one field tells them all apart', () => {
    const options = buildEditionSwitchOptions(
      physical({
        edition: '2nd',
        otherEditions: [
          edition({ id: 'b2', type: 'physical', publisher: 'Ace' }),
          edition({ id: 'b3', type: 'physical' }),
        ],
      }),
    );

    expect(options.map((o) => o.detail)).toEqual(['2nd edition', 'Ace', 'Copy 3']);
  });

  it('numbers copies only when the metadata is identical', () => {
    const options = buildEditionSwitchOptions(physical({ otherEditions: [edition({ id: 'b2', type: 'physical', title: 'Dune' })] }));

    expect(options.map((o) => o.detail)).toEqual(['Copy 1', 'Copy 2']);
  });

  it('carries progress for every edition, the current one included', () => {
    const options = buildEditionSwitchOptions(
      book({
        progressPercent: 42,
        otherEditions: [
          edition({ id: 'b2', type: 'audiobook', progressPercent: 100, finishedAt: '2026-08-02T08:00:00Z' }),
          edition({ id: 'b3', fileName: 'dune.pdf' }),
        ],
      }),
    );

    expect(options.map((o) => [o.format, o.progress, o.finished])).toEqual([
      ['Audiobook', 100, true],
      ['Ebook', 42, false],
      ['PDF', 0, false],
    ]);
    expect(options.map((o) => o.description)).toEqual(['Audiobook, finished', 'Ebook, 42% read', 'PDF, not started']);
  });

  it('describes every detail an edition has, not only the one shown', () => {
    const [audio] = buildEditionSwitchOptions(
      book({
        otherEditions: [
          edition({ id: 'b2', type: 'audiobook', narrator: 'Scott Brick', duration: '21h 2m', publishedDate: '2007', progressPercent: 37 }),
        ],
      }),
    );

    expect(audio.description).toBe('Audiobook (2007, read by Scott Brick, 21h 2m), 37% read');
  });

  it('carries each edition\'s own rating, never the book\'s, and says it in the tooltip in words', () => {
    const options = buildEditionSwitchOptions(
      book({
        rating: 5,
        editionRating: 3,
        progressPercent: 42,
        otherEditions: [edition({ id: 'b2', type: 'audiobook', rating: 2, progressPercent: 34 }), edition({ id: 'b3', fileName: 'dune.pdf' })],
      }),
    );

    expect(options.map((o) => [o.format, o.rating, o.ratingNoun])).toEqual([
      ['Audiobook', 2, 'narration'],
      ['Ebook', 3, 'this edition'],
      ['PDF', 0, 'this edition'],
    ]);
    expect(options.map((o) => o.description)).toEqual([
      'Audiobook, 34% read, narration rated 2 out of 5',
      'Ebook, 42% read, this edition rated 3 out of 5',
      'PDF, not started',
    ]);
  });
});

describe('editionChoices', () => {
  it('is empty for one edition, so a picker picks the book at once', () => {
    expect(editionChoices(book())).toEqual([]);
  });

  it('labels editions as the switcher does, with the detail for a shared format', () => {
    const choices = editionChoices(book({
      edition: '1st', otherEditions: [
        edition({ id: 'b2', edition: '2nd' }),
        edition({ id: 'b3', type: 'audiobook' }),
      ],
    }));
    expect(choices.map((c) => c.label)).toEqual(['Audiobook', 'Ebook · 1st edition', 'Ebook · 2nd edition']);
    expect(choices.map((c) => c.concept)).toEqual(['audiobook', 'ebook', 'ebook']);
  });
});
