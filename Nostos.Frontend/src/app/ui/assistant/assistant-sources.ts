/**
 * Sources and numbered citations for one transcript entry (#634, #1002).
 *
 * These are presentation-only derivations from the entry's inert artifacts and
 * its historical `sources` bridge. Keeping them here lets the entry component
 * render the sources row and the citation markers from the same numbers, with
 * the same grouping, without the panel shell owning either.
 */
import type {
  AssistantEntry,
  AssistantEvidenceReferenceDto,
  AssistantSourceReferenceDto,
  AssistantTurnArtifact,
} from './assistant.service';

export type EntrySourceItem =
  | {
      kind: 'evidence';
      key: string;
      /** Its citation number: the place of this evidence in the turn's list (issue #1002). */
      number: number;
      bookId: string | null;
      bookTitle: string;
      excerpt: string;
      groupableBook: boolean;
      evidence: AssistantEvidenceReferenceDto;
    }
  | {
      kind: 'legacy';
      key: string;
      bookId: string | null;
      bookTitle: string;
      excerpt: string;
      groupableBook: boolean;
      source: AssistantSourceReferenceDto;
    };

export type EntrySourceDisplay =
  | {
      kind: 'item';
      key: string;
      item: EntrySourceItem;
    }
  | {
      kind: 'group';
      key: string;
      bookId: string;
      bookTitle: string;
      items: EntrySourceItem[];
    };

export interface EntrySourceView {
  items: EntrySourceDisplay[];
}

/**
 * The citation numbers a grouped source row covers, compressed for the row:
 * `1–2` for a run, `1, 3` for markers that are not consecutive. Kept beside
 * `buildEntrySourceView` so a group and its inline markers cannot drift.
 */
export function formatCitationNumbers(numbers: readonly number[]): string {
  const sorted = [...new Set(numbers)].sort((a, b) => a - b);
  const parts: string[] = [];
  for (let index = 0; index < sorted.length; index += 1) {
    const start = sorted[index];
    let end = start;
    while (index + 1 < sorted.length && sorted[index + 1] === end + 1) {
      end = sorted[index + 1];
      index += 1;
    }
    parts.push(end > start ? `${start}–${end}` : String(start));
  }
  return parts.join(', ');
}

function canonicalEvidenceKey(evidence: AssistantEvidenceReferenceDto): string {
  const handle = evidence.handle;
  return [
    handle.kind,
    handle.noteId ?? '',
    handle.topicId ?? '',
    handle.bookId ?? '',
    handle.sourceSha256 ?? '',
    handle.extractorVersion ?? '',
    handle.ordinal ?? '',
  ].map(String).join('|');
}

function legacySourceKey(source: AssistantSourceReferenceDto): string {
  return `${source.bookId}|${source.sourceSha256}|${JSON.stringify(source.locators ?? [])}|${source.excerpt.slice(0, 320)}`;
}

/**
 * Builds the one transcript source surface for a completed entry.
 *
 * Canonical evidence wins whenever it exists. Legacy `entry.sources` is only
 * the historical bridge for entries that carry no evidence artifacts.
 */
export function buildEntrySourceView(entry: AssistantEntry): EntrySourceView {
  const evidenceArtifacts = (entry.artifacts ?? []).filter(
    (artifact): artifact is Extract<AssistantTurnArtifact, { kind: 'evidence' }> =>
      artifact.kind === 'evidence',
  );

  const sourceItems: EntrySourceItem[] = [];

  if (evidenceArtifacts.length > 0) {
    const seen = new Set<string>();

    for (const [index, artifact] of evidenceArtifacts.entries()) {
      const evidence = artifact.evidence;
      const dedupeKey = canonicalEvidenceKey(evidence);
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);

      const bookId = evidence.handle.bookId ?? null;
      sourceItems.push({
        kind: 'evidence',
        key: `evidence:${dedupeKey}`,
        number: index + 1,
        bookId,
        bookTitle: evidence.bookTitle?.trim() || evidence.label,
        excerpt: evidence.excerpt ?? '',
        groupableBook: evidence.handle.kind === 'book_text' && !!bookId,
        evidence,
      });
    }
  } else {
    const seen = new Set<string>();

    for (const source of entry.sources ?? []) {
      const dedupeKey = legacySourceKey(source);
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);

      sourceItems.push({
        kind: 'legacy',
        key: `legacy:${dedupeKey}`,
        bookId: source.bookId || null,
        bookTitle: source.bookTitle,
        excerpt: source.excerpt,
        groupableBook: !!source.bookId,
        source,
      });
    }
  }

  type PendingDisplay =
    | {
        kind: 'item';
        key: string;
        item: EntrySourceItem;
      }
    | {
        kind: 'book';
        key: string;
        bookId: string;
        bookTitle: string;
        items: EntrySourceItem[];
      };

  const pending: PendingDisplay[] = [];
  const bookGroupIndexes = new Map<string, number>();

  for (const item of sourceItems) {
    if (!item.groupableBook || !item.bookId) {
      pending.push({
        kind: 'item',
        key: item.key,
        item,
      });
      continue;
    }

    const existingIndex = bookGroupIndexes.get(item.bookId);
    if (existingIndex !== undefined) {
      const existing = pending[existingIndex];
      if (existing.kind === 'book') existing.items.push(item);
      continue;
    }

    bookGroupIndexes.set(item.bookId, pending.length);
    pending.push({
      kind: 'book',
      key: `book:${item.bookId}`,
      bookId: item.bookId,
      bookTitle: item.bookTitle,
      items: [item],
    });
  }

  return {
    items: pending.map((display): EntrySourceDisplay => {
      if (display.kind === 'item') return display;
      if (display.items.length === 1) {
        return {
          kind: 'item',
          key: display.items[0].key,
          item: display.items[0],
        };
      }

      return {
        kind: 'group',
        key: display.key,
        bookId: display.bookId,
        bookTitle: display.bookTitle,
        items: display.items,
      };
    }),
  };
}

/** Whether the reply cites its sources by number, so the sources row shows the numbers. */
export function citesSources(entry: AssistantEntry): boolean {
  return entry.kind === 'assistant' && /\[\^\d+\]/.test(entry.text);
}

/** Every canonical evidence artifact of a turn, in citation order. */
export function citedEvidence(entry: AssistantEntry): AssistantEvidenceReferenceDto[] {
  return (entry.artifacts ?? []).flatMap((artifact) =>
    artifact.kind === 'evidence' ? [artifact.evidence] : [],
  );
}

const citationPreviewCache = new WeakMap<AssistantEntry, readonly string[]>();

/** The hover preview for each citation number: where it is from and how it starts. */
export function citationPreviews(entry: AssistantEntry): readonly string[] {
  const cached = citationPreviewCache.get(entry);
  if (cached) return cached;
  const previews = citedEvidence(entry).map((evidence) => {
    const excerpt = sourceExcerpt(evidence.excerpt);
    return excerpt ? `${evidenceLabel(evidence)} — ${excerpt}` : evidenceLabel(evidence);
  });
  citationPreviewCache.set(entry, previews);
  return previews;
}

export function sourceExcerpt(excerpt: string | null | undefined): string {
  return (excerpt ?? '').slice(0, 120).trim();
}

export function evidenceLabel(evidence: AssistantEvidenceReferenceDto): string {
  const locator = evidence.locators?.[0];
  const title = evidence.bookTitle?.trim() || evidence.label;
  if (!locator) return evidence.label;
  if (locator.type === 'pdf' && locator.pdfPageIndex !== null && locator.pdfPageIndex !== undefined) {
    const page = locator.pdfPageLabel?.trim() || String(locator.pdfPageIndex + 1);
    return `${title} · p. ${page}`;
  }
  if (locator.type === 'epub') return `${title} · reading position`;
  return evidence.label;
}

export function sourceLabel(source: AssistantSourceReferenceDto): string {
  const locator = source.locators[0];
  if (!locator) return source.bookTitle;
  if (locator.type === 'pdf' && locator.pdfPageIndex !== null && locator.pdfPageIndex !== undefined) {
    const page = locator.pdfPageLabel?.trim() || String(locator.pdfPageIndex + 1);
    return `${source.bookTitle} · p. ${page}`;
  }
  if (locator.type === 'epub') return `${source.bookTitle} · reading position`;
  return source.bookTitle;
}

export function canOpenEvidence(
  evidence: AssistantEvidenceReferenceDto,
  hasRouter: boolean,
): boolean {
  if (!hasRouter) return false;
  if (evidence.handle.kind === 'note') return !!evidence.handle.noteId;
  if (evidence.handle.kind === 'topic') return !!evidence.handle.topicId;
  return !!evidence.handle.bookId
    && !!evidence.locators?.some((locator) => locator.type === 'pdf' || locator.type === 'epub');
}

export function canOpenSource(source: AssistantSourceReferenceDto, hasRouter: boolean): boolean {
  return hasRouter
    && !!source.locators?.some((locator) => locator.type === 'pdf' || locator.type === 'epub');
}
