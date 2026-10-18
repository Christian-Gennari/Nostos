/**
 * Semantic icon vocabulary — Nostos product concepts, not glyphs.
 *
 * WHY THIS EXISTS (and where it stops)
 * ------------------------------------
 * `<nostos-icon name="headphones" />` names a GLYPH. That is fine for a one-off
 * action, but a stable product concept must not be re-decided independently by
 * every surface: the Library's "audiobook" chip, the book row's format badge and
 * the audio reader's nav all mean the same thing, and they drift apart the moment
 * each picks its own glyph.
 *
 * So: a stable concept gets ONE entry here, and surfaces ask for the CONCEPT
 * (`<nostos-icon concept="audiobook" />`). This file is deliberately small. It is
 * a mapping, not a framework, and it must not grow to cover one-off actions —
 * those are named as glyphs at the call site.
 *
 * ADDING A CONCEPT
 * ----------------
 * Only when the concept is product-stable AND used in more than one place (or is
 * a top-level navigation destination). The target must be a registered glyph, so
 * `satisfies` fails the build otherwise.
 */

import type { NostosIconName } from './nostos-icons';

export const NOSTOS_CONCEPTS = {
  /** Top-level destinations — the app dock and the Library sidebar both render these. */
  readingRoom: 'book-open',
  library: 'books',
  studio: 'pen-nib',
  atlas: 'map-trifold',
  settings: 'gear-six',

  /** Book formats. One answer for a format, everywhere a format is shown. */
  audiobook: 'headphones',
  ebook: 'book-open',
  pdf: 'file-text',
  physical: 'bookmark-simple',

  /** Reading-room artefacts and annotations. */
  quote: 'quotes',
  note: 'note',
  /** A passage (issue #1014): a long stretch of a book kept to read again. */
  passage: 'scroll',
  /** A topic in the Brain: a name notes are gathered under, written [[Topic]]. */
  topic: 'hash',
  /** Notes with no topic yet: the Brain's Unsorted, like the Library's. */
  unsorted: 'tray',
  /** The Brain's map: topics as a graph, joined by the notes they share. */
  topicMap: 'graph',

  /** The writing studio's AI suggestion surface. */
  aiSuggestion: 'sparkle',

  /** A work's editions (the Layers -> Stack idea: one work, several stacked files). */
  editions: 'stack',
} as const satisfies Record<string, NostosIconName>;

/** The stable Nostos concepts an icon may be requested by. */
export type NostosConcept = keyof typeof NOSTOS_CONCEPTS;
