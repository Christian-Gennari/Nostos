import { cfiRangeEnds, cutSection, passageEnds, passagePlainText } from './epub-passage';

describe('EPUB passages (issue #1014)', () => {
  it('splits a range CFI into its two point ends', () => {
    expect(cfiRangeEnds('epubcfi(/6/14!/4/2,/1:0,/1:5)')).toEqual({
      start: 'epubcfi(/6/14!/4/2/1:0)',
      end: 'epubcfi(/6/14!/4/2/1:5)',
    });
    expect(cfiRangeEnds('epubcfi(/6/14!/4/2/1:3)')).toEqual({
      start: 'epubcfi(/6/14!/4/2/1:3)',
      end: 'epubcfi(/6/14!/4/2/1:3)',
    });
    expect(cfiRangeEnds('not a cfi')).toBeNull();
  });

  it('spans from the earlier start to the later end, whichever mark came first', () => {
    const later = { start: 'epubcfi(/6/6!/4/2/1:0)', end: 'epubcfi(/6/6!/4/8/1:40)' };
    const earlier = { start: 'epubcfi(/6/4!/4/2/1:0)', end: 'epubcfi(/6/4!/4/2/1:50)' };
    const expected = { start: earlier.start, end: later.end };
    expect(passageEnds(later, earlier)).toEqual(expected);
    expect(passageEnds(earlier, later)).toEqual(expected);
  });

  it('keeps paragraphs and line breaks, and drops the source file’s own wrapping', () => {
    const doc = new DOMParser().parseFromString(
      '<div><h2>Chapter V.<br>The Grand Inquisitor</h2><p>“Even this must\n  have a <em>preface</em>,”</p><p>laughed Ivan.</p><style>p{}</style></div>',
      'text/html',
    );
    expect(passagePlainText(doc.body.firstChild!)).toBe(
      'Chapter V.\nThe Grand Inquisitor\n\n“Even this must have a preface,”\n\nlaughed Ivan.',
    );
  });

  it('cuts a section from a point to its end', () => {
    const doc = new DOMParser().parseFromString(
      '<html><body><p>first</p><p>second</p></body></html>',
      'text/html',
    );
    const whole = cutSection(doc, null, null)!;
    const holder = doc.createElement('div');
    holder.appendChild(whole);
    expect(holder.textContent).toBe('firstsecond');
  });
});
