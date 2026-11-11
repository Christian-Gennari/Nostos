/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { Injectable, signal } from '@angular/core';

/**
 * Carries a dropped `.nostos` archive from the Import drop zone (issue #1526)
 * to the whole-library import flow on Settings → Backups & data.
 *
 * The flow lives at another route and is mounted by the settings page, so the
 * file waits here for the first flow that asks. `take()` hands it over once:
 * a page reload must not replay an import nobody chose.
 */
@Injectable({ providedIn: 'root' })
export class PortableArchiveHandoffService {
  /** The file waiting for the whole-library import flow, if any. */
  readonly pending = signal<File | null>(null);

  hand(file: File): void {
    this.pending.set(file);
  }

  /** The file waiting for the import flow, if any; it is cleared as it is taken. */
  take(): File | null {
    const file = this.pending();
    if (file) this.pending.set(null);
    return file;
  }
}
