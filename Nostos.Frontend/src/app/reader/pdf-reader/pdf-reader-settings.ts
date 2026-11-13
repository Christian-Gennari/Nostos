import { ScrollModeType } from 'ngx-extended-pdf-viewer';

/**
 * Page fits a fixed-layout document can be read at, offered in the shell's view
 * panel. A phone default of a fitted whole page renders a 512-page book at about
 * 9.5px, so 'page-width' leads; 100 is actual size (issue #226 §9).
 */
export const PDF_ZOOM_PRESETS: { value: string | number; label: string }[] = [
  { value: 'page-width', label: 'Fit width' },
  { value: 'page-fit', label: 'Whole page' },
  { value: 100, label: 'Actual size' },
];

export const DEFAULT_PDF_ZOOM: string | number = 'page-width';
export const DEFAULT_PDF_SCROLL_MODE: ScrollModeType = ScrollModeType.vertical;

export const PDF_READING_MODES: { value: ScrollModeType; label: string }[] = [
  { value: ScrollModeType.vertical, label: 'Scroll' },
  { value: ScrollModeType.page, label: 'Page' },
];

export function pdfInvertStorageKey(bookId: string): string {
  return `nostos.pdf-invert.${bookId}`;
}

export function readPdfInversion(bookId: string): boolean | null {
  try {
    const raw = localStorage.getItem(pdfInvertStorageKey(bookId));
    if (raw !== null) {
      return JSON.parse(raw) === true;
    }
  } catch {
    // Private-mode storage or malformed value — fall back to theme default.
  }
  return null;
}

export function savePdfInversion(bookId: string, value: boolean): void {
  try {
    localStorage.setItem(pdfInvertStorageKey(bookId), JSON.stringify(value));
  } catch {
    // Private-mode storage — the mode still applies for the session.
  }
}

export function pdfScrollStorageKey(bookId: string): string {
  return `nostos.pdf-scroll.${bookId}`;
}

export function readPdfScrollMode(bookId: string): ScrollModeType {
  try {
    const saved = localStorage.getItem(pdfScrollStorageKey(bookId));
    if (saved === 'page') return ScrollModeType.page;
    if (saved === 'scroll') return ScrollModeType.vertical;
  } catch {
    // Unreadable storage keeps the continuous default.
  }
  return DEFAULT_PDF_SCROLL_MODE;
}

export function savePdfScrollMode(bookId: string, mode: ScrollModeType): void {
  try {
    localStorage.setItem(
      pdfScrollStorageKey(bookId),
      mode === ScrollModeType.page ? 'page' : 'scroll',
    );
  } catch {
    // Private-mode storage can throw — the mode still applies for the session.
  }
}

export function pdfZoomStorageKey(bookId: string): string {
  return `nostos.pdf-zoom.${bookId}`;
}

export function readPdfZoom(bookId: string): string | number {
  try {
    const raw = localStorage.getItem(pdfZoomStorageKey(bookId));
    if (raw === null) return DEFAULT_PDF_ZOOM;
    const parsed = JSON.parse(raw) as unknown;
    if (typeof parsed === 'number' || typeof parsed === 'string') return parsed;
  } catch {
    // Unreadable storage falls back to the viewport default.
  }
  return DEFAULT_PDF_ZOOM;
}

export function savePdfZoom(bookId: string, zoom: string | number): void {
  try {
    localStorage.setItem(pdfZoomStorageKey(bookId), JSON.stringify(zoom));
  } catch {
    // Private-mode storage can throw — the zoom still applies for the session.
  }
}

export function nextPdfZoom(current: string | number, direction: 'in' | 'out'): number {
  if (direction === 'in') {
    return typeof current === 'number' ? Math.min(current + 10, 400) : 110;
  }
  return typeof current === 'number' ? Math.max(current - 10, 20) : 90;
}

export function isPdfZoomPreset(current: string | number, preset: string | number): boolean {
  if (typeof current === 'number' || typeof preset === 'number') return current === preset;
  return String(current).toLowerCase() === String(preset).toLowerCase();
}

export function formatPdfZoomLabel(current: string | number): string {
  if (typeof current === 'number') return `${current}%`;
  return current === 'page-width' ? 'Fit width' : 'Whole page';
}
