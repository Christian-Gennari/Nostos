/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
import type { Book, EditionSummaryDto } from '../core/dtos/book.dtos';
import type { EditionSwitcherOption } from '../ui/edition-switcher/edition-switcher.component';

type Format = Pick<EditionSwitcherOption, 'format' | 'concept'>;

/** The fields an edition can be told apart by, from either side of the DTO split. */
interface EditionFacts {
  id: string;
  current: boolean;
  type: string;
  format?: string | null;
  fileName?: string | null;
  title?: string | null;
  edition?: string | null;
  publisher?: string | null;
  publishedDate?: string | null;
  narrator?: string | null;
  duration?: string | null;
  progressPercent?: number | null;
  finishedAt?: string | null;
}

/**
 * One way to tell editions of the same format apart, in the order a reader
 * names them: "the 2nd edition", "the 1990 one", "the Stephen Fry one". Each
 * returns the visible detail, or null when the edition does not carry it.
 */
const DISCRIMINATORS: ((edition: EditionFacts) => string | null)[] = [
  (e) => editionText(e.edition),
  (e) => yearOf(e.publishedDate),
  (e) => (isAudio(e.type) && clean(e.narrator) ? `Read by ${clean(e.narrator)}` : null),
  (e) => clean(e.publisher),
  (e) => (isAudio(e.type) ? clean(e.duration) : null),
  (e) => clean(e.title),
];

/**
 * The book page's edition switcher options, current edition included (#1086).
 *
 * Ordered by format then id, not current-first, so the options hold still when
 * the reader switches. An edition whose format is unique needs no detail. Two
 * editions of one format get the first detail that tells the whole group apart
 * (edition, year, narrator, publisher, duration, title), so they read as "EPUB ·
 * 2nd edition" and "EPUB · 3rd edition" rather than "EPUB 1" and "EPUB 2". Only
 * when the metadata is identical do they fall back to "Copy 1", "Copy 2".
 */
export function buildEditionSwitchOptions(book: Book | null): EditionSwitcherOption[] {
  const others = book?.otherEditions ?? [];
  if (!book || others.length === 0) return [];

  const all = [
    toFacts(book, true),
    ...others.filter((edition) => edition.id !== book.id).map((edition) => toFacts(edition, false)),
  ].map((facts) => ({ facts, ...formatOf(facts) }));

  all.sort((a, b) => a.format.localeCompare(b.format) || a.facts.id.localeCompare(b.facts.id));

  const details = new Map<string, string | null>();
  for (const format of new Set(all.map((entry) => entry.format))) {
    const group = all.filter((entry) => entry.format === format);
    if (group.length < 2) continue;
    const labels = distinguish(group.map((entry) => entry.facts));
    group.forEach((entry, i) => details.set(entry.facts.id, labels[i]));
  }

  return all.map(({ facts, format, concept }) => {
    const detail = details.get(facts.id) ?? null;
    const finished = !!facts.finishedAt;
    const progress = clamp(facts.progressPercent ?? 0);
    return {
      id: facts.id,
      format,
      concept,
      detail,
      current: facts.current,
      progress,
      finished,
      description: describe(facts, format, detail, progress, finished),
    };
  });
}

/**
 * The first discriminator whose values are present on every edition of the
 * group and all different, so the group reads as one kind of detail. Failing
 * that, each edition gets its own first detail no other edition shares, and
 * only an edition with none falls back to its position.
 */
function distinguish(group: EditionFacts[]): string[] {
  const candidates = DISCRIMINATORS.map((pick) => group.map(pick));

  const exact = candidates.find(
    (values) => values.every((value) => !!value) && new Set(values).size === values.length,
  );
  if (exact) return exact as string[];

  return group.map((_, i) => {
    const unique = candidates.find((values) => {
      const value = values[i];
      return !!value && values.indexOf(value) === values.lastIndexOf(value);
    });
    return unique?.[i] ?? `Copy ${i + 1}`;
  });
}

function toFacts(source: Book | EditionSummaryDto, current: boolean): EditionFacts {
  return {
    id: source.id,
    current,
    type: source.type,
    format: 'format' in source ? source.format : undefined,
    fileName: source.fileName,
    title: source.title,
    edition: source.edition,
    publisher: source.publisher,
    publishedDate: source.publishedDate,
    narrator: source.narrator,
    duration: source.duration,
    progressPercent: source.progressPercent,
    finishedAt: source.finishedAt,
  };
}

/** The short format name and its glyph concept: Audiobook, EPUB, PDF, Physical. */
export function formatOf(edition: Pick<EditionFacts, 'type' | 'format' | 'fileName'>): Format {
  const type = edition.type.toLowerCase();
  if (isAudio(type)) return { format: 'Audiobook', concept: 'audiobook' };
  if (type === 'physical') return { format: 'Physical', concept: 'physical' };

  const extension = edition.fileName?.split('.').pop()?.trim().toLowerCase();
  const declared = edition.format?.trim().toLowerCase();
  const kind = extension || (declared && declared !== 'ebook' ? declared : 'epub');
  if (kind === 'pdf') return { format: 'PDF', concept: 'pdf' };
  return { format: kind.toUpperCase(), concept: 'ebook' };
}

/** Tooltip and menu hint: every detail the edition has, not only the one shown. */
function describe(facts: EditionFacts, format: string, detail: string | null, progress: number, finished: boolean): string {
  const extras = [
    editionText(facts.edition),
    yearOf(facts.publishedDate),
    clean(facts.publisher),
    isAudio(facts.type) && clean(facts.narrator) ? `read by ${clean(facts.narrator)}` : null,
    isAudio(facts.type) ? clean(facts.duration) : null,
  ].filter((value): value is string => !!value);
  // A title or "Copy 2" is the one shown detail the list above does not carry.
  if (detail && !extras.some((extra) => extra.toLowerCase() === detail.toLowerCase())) extras.unshift(detail);

  const status = finished ? 'finished' : progress > 0 ? `${progress}% read` : 'not started';
  return `${format}${extras.length ? ` (${extras.join(', ')})` : ''}, ${status}`;
}

/** "2nd" reads as a label only with its noun; "Revised edition" already has it. */
function editionText(value: string | null | undefined): string | null {
  const text = clean(value);
  if (!text) return null;
  return /edition|ed\.$/i.test(text) ? text : `${text} edition`;
}

function yearOf(value: string | null | undefined): string | null {
  return clean(value)?.match(/\b(\d{4})\b/)?.[1] ?? null;
}

function clean(value: string | null | undefined): string | null {
  const text = value?.trim();
  return text ? text : null;
}

function isAudio(type: string): boolean {
  const normalized = type.toLowerCase();
  return normalized === 'audiobook' || normalized === 'audio';
}

function clamp(value: number): number {
  return Math.max(0, Math.min(100, Math.round(value)));
}
