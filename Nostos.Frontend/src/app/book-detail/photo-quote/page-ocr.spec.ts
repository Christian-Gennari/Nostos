import { OcrWord, selectionText, tesseractLanguage, textBounds, toWords } from './page-ocr';

describe('photo quote OCR helpers (issue #1292)', () => {
  it('maps a book language to a Tesseract language, English by default', () => {
    expect(tesseractLanguage('en')).toBe('eng');
    expect(tesseractLanguage('en-GB')).toBe('eng');
    expect(tesseractLanguage('sv')).toBe('swe');
    expect(tesseractLanguage('Swedish')).toBe('swe');
    expect(tesseractLanguage('deu')).toBe('deu');
    expect(tesseractLanguage('nb_NO')).toBe('nor');
    expect(tesseractLanguage(null)).toBe('eng');
    expect(tesseractLanguage('klingon')).toBe('eng');
  });

  it('keeps the page words in reading order with their paragraph and line', () => {
    const words = toWords([
      {
        paragraphs: [
          { lines: [{ words: [{ text: 'The', confidence: 96 }, { text: 'time', confidence: 91 }] }, { words: [{ text: 'of', confidence: 40 }] }] },
          { lines: [{ words: [{ text: ' ', confidence: 0 }, { text: 'Next.', confidence: 88 }] }] },
        ],
      },
    ]);

    expect(words).toEqual([
      { text: 'The', confidence: 96, paragraph: 0, line: 0 },
      { text: 'time', confidence: 91, paragraph: 0, line: 0 },
      { text: 'of', confidence: 40, paragraph: 0, line: 1 },
      { text: 'Next.', confidence: 88, paragraph: 1, line: 2 },
    ]);
    expect(toWords(null)).toEqual([]);
  });

  const page: OcrWord[] = [
    { text: 'They', confidence: 90, paragraph: 0, line: 0 },
    { text: 'waited', confidence: 90, paragraph: 0, line: 0 },
    { text: 'by', confidence: 90, paragraph: 0, line: 0 },
    { text: 'the', confidence: 90, paragraph: 0, line: 0 },
    { text: 'moun-', confidence: 90, paragraph: 0, line: 0 },
    { text: 'tain', confidence: 90, paragraph: 0, line: 1 },
    { text: 'pass.', confidence: 90, paragraph: 0, line: 1 },
    { text: 'Nobody', confidence: 90, paragraph: 1, line: 2 },
    { text: 'came.', confidence: 90, paragraph: 1, line: 2 },
  ];

  it('joins the selected words as the book has them', () => {
    expect(selectionText(page, 0, 6)).toBe('They waited by the mountain pass.');
    expect(selectionText(page, 6, 1)).toBe('waited by the mountain pass.');
    expect(selectionText(page, 5, 8)).toBe('tain pass.\n\nNobody came.');
    expect(selectionText(page, 4, 4)).toBe('moun-');
  });

  it('keeps a hyphen that is not a line break, and one before a capital', () => {
    const words: OcrWord[] = [
      { text: 'well-', confidence: 90, paragraph: 0, line: 0 },
      { text: 'Known', confidence: 90, paragraph: 0, line: 1 },
    ];
    expect(selectionText(words, 0, 1)).toBe('well- Known');
  });

  it('crops a photo to its text block', () => {
    const width = 100;
    const height = 100;
    const grey = new Uint8ClampedArray(width * height).fill(230);
    // A block of "text" from x 30–69, y 40–59 on a light page.
    for (let y = 40; y < 60; y++) for (let x = 30; x < 70; x += 2) grey[y * width + x] = 20;

    const crop = textBounds(grey, width, height);
    expect(crop.x).toBeLessThanOrEqual(30);
    expect(crop.x).toBeGreaterThan(20);
    expect(crop.y).toBeLessThanOrEqual(40);
    expect(crop.x + crop.width).toBeGreaterThanOrEqual(69);
    expect(crop.y + crop.height).toBeGreaterThanOrEqual(60);
    expect(crop.width).toBeLessThan(60);
  });

  it('keeps the whole photo when nothing stands out', () => {
    const grey = new Uint8ClampedArray(64 * 64).fill(200);
    expect(textBounds(grey, 64, 64)).toEqual({ x: 0, y: 0, width: 64, height: 64 });
  });
});
