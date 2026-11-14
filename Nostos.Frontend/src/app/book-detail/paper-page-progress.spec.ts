import { describe, expect, it } from 'vitest';

import {
  clampPaperPage,
  paperPagePercent,
  parsePaperPage,
  readPaperPage,
  writePaperPage,
} from './paper-page-progress';

describe('paper page storage (issue #1091)', () => {
  it('round-trips a page through the paper location shape', () => {
    expect(writePaperPage(143)).toBe('{"page":143}');
    expect(readPaperPage(writePaperPage(143))).toBe(143);
  });

  it.each([
    ['null', null, null],
    ['undefined', undefined, null],
    ['an empty string', '', null],
    ['a reader location', 'epubcfi(/6/4)', null],
    ['a PDF page number only', '{"pageNumber":3}', null],
    ['a page of zero', '{"page":0}', null],
    ['a fractional page', '{"page":1.5}', null],
    ['a string page', '{"page":"7"}', null],
    ['a bare number', '1234', null],
  ])('reads no page from %s', (_case, raw, expected) => {
    expect(readPaperPage(raw as string | null | undefined)).toBe(expected);
  });

  it('reads a typed page only when it is a whole number from 1', () => {
    expect(parsePaperPage('143')).toBe(143);
    expect(parsePaperPage('  12  ')).toBe(12);
    expect(parsePaperPage('1')).toBe(1);
    expect(parsePaperPage('999999')).toBe(999999);
  });

  it.each(['0', '-4', '12.5', 'abc', '', '   ', '1234567', null, undefined])(
    'reads no page from a typed %s',
    (typed) => {
      expect(parsePaperPage(typed as string | null | undefined)).toBeNull();
    },
  );

  it('clamps a page inside the book, and to 1 without a page count', () => {
    expect(clampPaperPage(0, 880)).toBe(1);
    expect(clampPaperPage(-4, 880)).toBe(1);
    expect(clampPaperPage(900, 880)).toBe(880);
    expect(clampPaperPage(12.7, 880)).toBe(12);
    expect(clampPaperPage(5000, null)).toBe(5000);
    expect(clampPaperPage(5000, 0)).toBe(5000);
  });

  it('derives the PDF reader’s percentage, never below 1 and never above 100', () => {
    expect(paperPagePercent(143, 880)).toBe(16);
    expect(paperPagePercent(880, 880)).toBe(100);
    expect(paperPagePercent(1, 880)).toBe(1);
  });

  it('stores no percentage without a page count, and never one above 100', () => {
    expect(paperPagePercent(50, null)).toBe(0);
    expect(paperPagePercent(50, 0)).toBe(0);
    expect(paperPagePercent(900, 880)).toBe(100);
  });
});
