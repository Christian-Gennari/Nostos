// src/app/reader/reader.interface.ts
import { Signal } from '@angular/core';

export interface TocItem {
  label: string;
  target: string | number; // CFI (epub), Page Number (pdf), or Timestamp (audio)
  children?: TocItem[];
}

export interface ReaderProgress {
  label: string; // "Page 5 of 200" or "05:12 / 14:00 or 12% of book"
  percentage: number; // 0 to 100 (for progress bars)
  tooltip?: string; // "1% ≈ 4 min" (The pop-up hint) (For ebook)
  pageNumber?: number; // Current page number (For PDF readers)
  pageCount?: number; // Total number of pages (For PDF readers)
  /** Printed/logical PDF PageLabels value, presentation only; never a navigation key. */
  pageLabel?: string | null;
}

export interface ReaderSourceTarget {
  type: 'pdf' | 'epub';
  pdfPage?: number;
  pdfPageLabel?: string | null;
  epubCfi?: string | null;
  epubResourceHref?: string | null;
  epubSpineIndex?: number | null;
  epubTextOffset?: number | null;
  excerpt?: string | null;
}

export interface IReader {
  // Navigation
  next(): void;
  previous(): void;
  goTo(target: string | number): void;
  goToSource?(target: ReaderSourceTarget): void | Promise<void>;

  // Data Extraction
  getCurrentLocation(): string | null; // For saving notes

  // View Controls
  zoomIn(): void;
  zoomOut(): void;

  /**
   * Open the reader's own search UI, when the format has one. Optional because
   * EPUB and audio do not implement it yet; the shell only offers the control
   * for formats that do, so the reader is never handed a button that does
   * nothing. (PDF: the viewer's find bar — @see PdfReader.openSearch.)
   */
  openSearch?(): void;

  // Highlight Management
  removeHighlight(identifier: string): void;
  /** Saves the pending mark; `content` is the note text (EPUB "Add note", #650). */
  commitHighlight(content?: string): void;
  discardHighlight(): void;

  // Reactive State
  toc: Signal<TocItem[]>;
  progress: Signal<ReaderProgress>;
  currentLocationTarget?: Signal<string | number | null>;
}
