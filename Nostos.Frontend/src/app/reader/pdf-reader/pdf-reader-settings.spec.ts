import { ScrollModeType } from 'ngx-extended-pdf-viewer';
import {
  DEFAULT_PDF_SCROLL_MODE,
  DEFAULT_PDF_ZOOM,
  formatPdfZoomLabel,
  isPdfZoomPreset,
  nextPdfZoom,
  readPdfInversion,
  readPdfScrollMode,
  readPdfZoom,
  savePdfInversion,
  savePdfScrollMode,
  savePdfZoom,
} from './pdf-reader-settings';

describe('pdf-reader-settings', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('reads and writes per-book zoom, scroll mode, and inversion independently', () => {
    expect(readPdfZoom('book-a')).toBe(DEFAULT_PDF_ZOOM);
    expect(readPdfScrollMode('book-a')).toBe(DEFAULT_PDF_SCROLL_MODE);
    expect(readPdfInversion('book-a')).toBeNull();

    savePdfZoom('book-a', 140);
    savePdfScrollMode('book-a', ScrollModeType.page);
    savePdfInversion('book-a', false);

    expect(readPdfZoom('book-a')).toBe(140);
    expect(readPdfScrollMode('book-a')).toBe(ScrollModeType.page);
    expect(readPdfInversion('book-a')).toBe(false);

    // Another book with no saved choices falls back to defaults.
    expect(readPdfZoom('book-b')).toBe(DEFAULT_PDF_ZOOM);
    expect(readPdfScrollMode('book-b')).toBe(DEFAULT_PDF_SCROLL_MODE);
    expect(readPdfInversion('book-b')).toBeNull();
  });

  it('handles malformed or throwing storage gracefully', () => {
    localStorage.setItem('nostos.pdf-zoom.bad', '{not-json');
    expect(readPdfZoom('bad')).toBe(DEFAULT_PDF_ZOOM);

    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage blocked');
    });

    expect(readPdfZoom('x')).toBe(DEFAULT_PDF_ZOOM);
    expect(readPdfScrollMode('x')).toBe(DEFAULT_PDF_SCROLL_MODE);
    expect(readPdfInversion('x')).toBeNull();
    expect(() => savePdfZoom('x', 120)).not.toThrow();
    expect(() => savePdfScrollMode('x', ScrollModeType.page)).not.toThrow();
    expect(() => savePdfInversion('x', true)).not.toThrow();
  });

  it('computes zoom steps, preset matching, and labels', () => {
    expect(nextPdfZoom('page-width', 'in')).toBe(110);
    expect(nextPdfZoom('page-width', 'out')).toBe(90);
    expect(nextPdfZoom(395, 'in')).toBe(400);
    expect(nextPdfZoom(25, 'out')).toBe(20);

    expect(isPdfZoomPreset('page-width', 'PAGE-WIDTH')).toBe(true);
    expect(isPdfZoomPreset(100, 'page-width')).toBe(false);
    expect(isPdfZoomPreset(100, 100)).toBe(true);

    expect(formatPdfZoomLabel('page-width')).toBe('Fit width');
    expect(formatPdfZoomLabel('page-fit')).toBe('Whole page');
    expect(formatPdfZoomLabel(125)).toBe('125%');
  });
});
