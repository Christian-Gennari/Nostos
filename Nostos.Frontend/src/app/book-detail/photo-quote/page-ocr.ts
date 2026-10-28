import { Injectable } from '@angular/core';

/**
 * Reading a photographed book page on the device (issue #1292). Tesseract runs
 * in a web worker in the browser: nothing leaves the device, it works with AI
 * off, and after the first use it works offline (the service worker keeps the
 * engine, Tesseract keeps the language data in IndexedDB).
 *
 * The engine (~4 MB) and language data load only when a photo is read, never
 * with the app.
 */

/** One recognised word and where it sits in the page's text. */
export interface OcrWord {
  text: string;
  /** 0–100, as Tesseract reports it. */
  confidence: number;
  paragraph: number;
  line: number;
}

/** Below this, a word is marked so a misread is easy to spot. */
export const LOW_CONFIDENCE = 70;

/** Language data shipped with Nostos; any other language is fetched once on first use. */
export const BUNDLED_LANGUAGES = ['eng', 'swe'] as const;

const ISO_639_1: Record<string, string> = {
  en: 'eng', sv: 'swe', de: 'deu', fr: 'fra', es: 'spa', it: 'ita', nl: 'nld',
  da: 'dan', no: 'nor', nb: 'nor', nn: 'nor', fi: 'fin', pt: 'por', pl: 'pol',
  ru: 'rus', is: 'isl', cs: 'ces', el: 'ell', la: 'lat', tr: 'tur', hu: 'hun',
};

const NAMES: Record<string, string> = {
  english: 'eng', swedish: 'swe', svenska: 'swe', german: 'deu', deutsch: 'deu',
  french: 'fra', spanish: 'spa', italian: 'ita', dutch: 'nld', danish: 'dan',
  norwegian: 'nor', finnish: 'fin', portuguese: 'por', polish: 'pol', russian: 'rus',
  icelandic: 'isl', latin: 'lat',
};

const TESSERACT_CODES = new Set(Object.values(ISO_639_1));

/**
 * The Tesseract language for a book's language field ("en", "en-GB", "sv",
 * "Swedish", "eng"). English when it is missing or not one Nostos knows.
 */
export function tesseractLanguage(bookLanguage: string | null | undefined): string {
  const value = (bookLanguage ?? '').trim().toLowerCase();
  if (!value) return 'eng';
  if (TESSERACT_CODES.has(value)) return value;
  const primary = value.split(/[-_]/)[0];
  return ISO_639_1[primary] ?? NAMES[value] ?? 'eng';
}

/** Tesseract's page structure, as far as words are concerned. */
interface OcrBlock {
  paragraphs: { lines: { words: { text: string; confidence: number }[] }[] }[];
}

/** The page's words in reading order, with their paragraph and line. */
export function toWords(blocks: readonly OcrBlock[] | null | undefined): OcrWord[] {
  const words: OcrWord[] = [];
  let paragraph = 0;
  let line = 0;
  for (const block of blocks ?? []) {
    for (const para of block.paragraphs ?? []) {
      for (const row of para.lines ?? []) {
        for (const word of row.words ?? []) {
          const text = word.text.trim();
          if (text) words.push({ text, confidence: word.confidence, paragraph, line });
        }
        line++;
      }
      paragraph++;
    }
  }
  return words;
}

/**
 * The text of words `from`..`to` (inclusive, either order) as the book has it:
 * lines of a paragraph run together, a word hyphenated across a line break is
 * joined, and paragraphs stay apart.
 */
export function selectionText(words: readonly OcrWord[], from: number, to: number): string {
  const start = Math.max(0, Math.min(from, to));
  const end = Math.min(words.length - 1, Math.max(from, to));
  let text = '';
  for (let i = start; i <= end; i++) {
    const word = words[i];
    if (i > start) {
      const previous = words[i - 1];
      if (word.paragraph !== previous.paragraph) {
        text += '\n\n';
      } else if (word.line !== previous.line && /[A-Za-zÀ-ÿ]-$/.test(text) && /^[a-zà-ÿ]/.test(word.text)) {
        text = text.slice(0, -1);
      } else {
        text += ' ';
      }
    }
    text += word.text;
  }
  return text;
}

/** A crop rectangle in pixels. */
export interface Crop {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * The part of a grey page that holds text: rows and columns with enough dark
 * pixels, plus a margin. A phone photo of a page has table, fingers and the
 * facing page around it, and Tesseract reads better without them. Falls back to
 * the whole image when nothing stands out.
 */
export function textBounds(grey: Uint8ClampedArray, width: number, height: number, margin = 0.03): Crop {
  const whole = { x: 0, y: 0, width, height };
  if (width < 16 || height < 16) return whole;

  let sum = 0;
  for (let i = 0; i < grey.length; i++) sum += grey[i];
  const dark = (sum / grey.length) * 0.7;

  const rows = new Uint32Array(height);
  const cols = new Uint32Array(width);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (grey[y * width + x] < dark) {
        rows[y]++;
        cols[x]++;
      }
    }
  }

  const span = (counts: Uint32Array, across: number): [number, number] | null => {
    const needed = Math.max(2, across * 0.01);
    let first = -1;
    let last = -1;
    for (let i = 0; i < counts.length; i++) {
      if (counts[i] >= needed) {
        if (first < 0) first = i;
        last = i;
      }
    }
    return first < 0 ? null : [first, last];
  };

  const ys = span(rows, width);
  const xs = span(cols, height);
  if (!ys || !xs) return whole;

  const padX = Math.round(width * margin);
  const padY = Math.round(height * margin);
  const x = Math.max(0, xs[0] - padX);
  const y = Math.max(0, ys[0] - padY);
  const crop = {
    x,
    y,
    width: Math.min(width, xs[1] + padX + 1) - x,
    height: Math.min(height, ys[1] + padY + 1) - y,
  };
  // A crop that keeps almost everything is not worth the loss of context.
  return crop.width * crop.height > width * height * 0.9 ? whole : crop;
}

const MAX_SIDE = 2400;

/**
 * The photo turned upright (EXIF), scaled to a size Tesseract reads well,
 * greyed, contrast-stretched and cropped to the text.
 */
export async function preparePage(photo: Blob): Promise<HTMLCanvasElement> {
  const bitmap = await createImageBitmap(photo, { imageOrientation: 'from-image' });
  const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height));
  const width = Math.max(1, Math.round(bitmap.width * scale));
  const height = Math.max(1, Math.round(bitmap.height * scale));

  const full = document.createElement('canvas');
  full.width = width;
  full.height = height;
  const context = full.getContext('2d', { willReadFrequently: true })!;
  context.drawImage(bitmap, 0, 0, width, height);
  bitmap.close();

  const image = context.getImageData(0, 0, width, height);
  const grey = new Uint8ClampedArray(width * height);
  let min = 255;
  let max = 0;
  for (let i = 0, p = 0; i < grey.length; i++, p += 4) {
    const value = (image.data[p] * 299 + image.data[p + 1] * 587 + image.data[p + 2] * 114) / 1000;
    grey[i] = value;
    if (value < min) min = value;
    if (value > max) max = value;
  }
  const range = Math.max(1, max - min);
  for (let i = 0, p = 0; i < grey.length; i++, p += 4) {
    const stretched = ((grey[i] - min) * 255) / range;
    grey[i] = stretched;
    image.data[p] = image.data[p + 1] = image.data[p + 2] = stretched;
  }
  context.putImageData(image, 0, 0);

  const crop = textBounds(grey, width, height);
  if (crop.width === width && crop.height === height) return full;

  const cropped = document.createElement('canvas');
  cropped.width = crop.width;
  cropped.height = crop.height;
  cropped.getContext('2d')!.drawImage(full, crop.x, crop.y, crop.width, crop.height, 0, 0, crop.width, crop.height);
  return cropped;
}

/** Reads one photographed page. Each call starts and ends its own worker. */
@Injectable({ providedIn: 'root' })
export class PageOcrService {
  async read(photo: Blob, language: string, progress: (fraction: number) => void = () => {}): Promise<OcrWord[]> {
    const page = await preparePage(photo);
    // tesseract.js is CommonJS: the bundled import holds it under `default`.
    const loaded = await import('tesseract.js');
    const { createWorker, OEM } = (loaded as unknown as { default?: typeof loaded }).default ?? loaded;
    const asset = (path: string) => new URL(`assets/tesseract/${path}`, document.baseURI).href;
    const bundled = (BUNDLED_LANGUAGES as readonly string[]).includes(language);

    const worker = await createWorker(language, OEM.LSTM_ONLY, {
      workerPath: asset('worker.min.js'),
      corePath: asset('core'),
      // Other languages come once from the same CDN Tesseract uses by default.
      langPath: bundled ? asset('lang') : 'https://cdn.jsdelivr.net/npm/@tesseract.js-data/' + language + '/4.0.0_best_int',
      gzip: true,
      workerBlobURL: false,
      logger: (message) => {
        if (message.status === 'recognizing text') progress(message.progress);
      },
    });

    try {
      const { data } = await worker.recognize(page, {}, { blocks: true, text: false });
      return toWords(data.blocks as OcrBlock[] | null);
    } finally {
      await worker.terminate();
    }
  }
}
