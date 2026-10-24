import {
  DEFAULT_HIGHLIGHT_COLOUR,
  HIGHLIGHT_COLOURS,
  asHighlightColour,
  highlightColourKey,
  highlightFillRef,
  highlightFillVar,
  noteHighlightColour,
  quotePen,
  readHighlightColour,
  resolveHighlightFill,
  writeHighlightColour,
} from './highlight-colours';

/**
 * The reader's highlighter pens (issue #208). These tests pin the three things a
 * future refactor could quietly break: a colour is named rather than hardcoded as
 * a hex, the choice is remembered per book, and an unknown stored value degrades
 * to the default instead of producing an invisible highlight.
 */
describe('highlight colours', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  it('offers four named pens, with sage as the default', () => {
    expect(HIGHLIGHT_COLOURS.map((option) => option.id)).toEqual(['sage', 'amber', 'blue', 'rose']);
    expect(HIGHLIGHT_COLOURS.every((option) => !!option.label)).toBe(true);
    expect(DEFAULT_HIGHLIGHT_COLOUR).toBe('sage');
  });

  it('derives token names from the colour, so no hex lives in code', () => {
    expect(highlightFillVar('sage')).toBe('--highlight-sage');
    expect(highlightFillRef('sage')).toBe('var(--highlight-sage)');
  });

  it('falls back to the default for anything that is not a known pen', () => {
    // localStorage can hold a value from an older build or another tab.
    expect(asHighlightColour('chartreuse')).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(asHighlightColour(null)).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(asHighlightColour('rose')).toBe('rose');
  });

  it('remembers the pen per book, not globally', () => {
    writeHighlightColour('book-a', 'blue');
    writeHighlightColour('book-b', 'rose');

    expect(readHighlightColour('book-a')).toBe('blue');
    expect(readHighlightColour('book-b')).toBe('rose');
    // A book never given a pen starts on the default.
    expect(readHighlightColour('book-c')).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(highlightColourKey('book-a')).toBe('nostos.highlight.book-a');
  });

  it('resolves the fill from the stylesheet, which is where the value lives', () => {
    const root = document.documentElement;
    root.style.setProperty('--highlight-sage', '#b7e4cf');
    try {
      expect(resolveHighlightFill('sage', '#fallback')).toBe('#b7e4cf');
      // An unset pen falls back rather than handing a raw `var()` to a canvas.
      expect(resolveHighlightFill('rose', '#fallback')).toBe('#fallback');
      expect(resolveHighlightFill('rose', '#fallback', null)).toBe('#fallback');
    } finally {
      root.style.removeProperty('--highlight-sage');
    }
  });

  it("draws a saved highlight in the note's own pen (issue #861)", () => {
    expect(noteHighlightColour({ highlightColour: 'rose', cfiRange: 'epubcfi(/6/4)' })).toBe('rose');
    // The stored field wins over a PDF payload's older embedded colour, which
    // a recolour leaves untouched.
    expect(
      noteHighlightColour({
        highlightColour: 'blue',
        cfiRange: JSON.stringify({ pageNumber: 2, rects: [], colour: 'sage' }),
      }),
    ).toBe('blue');
  });

  it('falls back to a PDF payload colour, then to the default pen', () => {
    // A PDF highlight older than the field kept its pen in the location JSON.
    expect(
      noteHighlightColour({ cfiRange: JSON.stringify({ pageNumber: 2, rects: [], colour: 'sage' }) }),
    ).toBe('sage');
    // An EPUB highlight with no recorded pen, and an import, are sage.
    expect(noteHighlightColour({ cfiRange: 'epubcfi(/6/4)' })).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(noteHighlightColour({ highlightColour: null })).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(noteHighlightColour({ highlightColour: 'gray' })).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    expect(noteHighlightColour({ cfiRange: '{not json' })).toBe(DEFAULT_HIGHLIGHT_COLOUR);
  });

  it('shows a quote in its pen only when the pen is known (issue #1203)', () => {
    expect(quotePen({ selectedText: 'Sing', highlightColour: 'rose' })).toBe('rose');
    // Recorded as none: the pen the highlight is drawn in.
    expect(quotePen({ selectedText: 'Sing', highlightColour: null })).toBe(DEFAULT_HIGHLIGHT_COLOUR);
    // A payload without the field, or a note without a quotation: neutral.
    expect(quotePen({ selectedText: 'Sing' })).toBeNull();
    expect(quotePen({ selectedText: '', highlightColour: 'amber' })).toBeNull();
  });
});
