/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import type { Book } from '../core/dtos/book.dtos';
import type { LibraryPreferencesService } from '../core/services/library-preferences.service';

/**
 * Where a shelf item links: the edition the reader last had open for this work,
 * or no link at all while Select mode owns the press (a tap ticks the book
 * instead of leaving the shelf).
 *
 * The two views and the page that hosts them must agree on this, and they did
 * not have a shared home for it before the list and grid were extracted — the
 * page owned it and both views were in the page's template.
 */
export function libraryBookLink(
  book: Book,
  selecting: boolean,
  preferences: LibraryPreferencesService,
): string[] | null {
  return selecting ? null : ['/library', preferences.getActiveEditionId(book.workId, book.id)];
}
