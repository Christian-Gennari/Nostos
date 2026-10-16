import { anchorAroundHit, offsetAnchor, rectContains } from './highlight-hit';

describe('highlight hit-testing', () => {
  const line = (top: number, left: number, right: number) => ({ top, bottom: top + 20, left, right });

  it('counts a point on a line box, or a pixel or two off its edge, as on the mark', () => {
    const rect = line(100, 50, 250);
    expect(rectContains(rect, 120, 110)).toBe(true);
    expect(rectContains(rect, 251, 121)).toBe(true);
    expect(rectContains(rect, 260, 110)).toBe(false);
    expect(rectContains(rect, 120, 130)).toBe(false);
  });

  it('opens the card around the lines of the mark that share the hit line’s column', () => {
    // Three lines in the left column; the passage then runs on in the next one.
    const first = line(100, 300, 460);
    const middle = line(120, 40, 460);
    const last = line(140, 40, 120);
    const overflow = line(40, 520, 700);

    const anchor = anchorAroundHit([first, middle, last, overflow], last);

    expect(anchor).toEqual({ top: 100, bottom: 160, left: 40, right: 460 });
  });

  it('keeps a one-line mark to its own box', () => {
    const only = line(100, 50, 250);
    expect(anchorAroundHit([only], only)).toEqual({ top: 100, bottom: 120, left: 50, right: 250 });
  });

  it('moves a box measured in a frame into the page', () => {
    expect(offsetAnchor({ top: 1, bottom: 2, left: 3, right: 4 }, 10, 20)).toEqual({
      top: 21,
      bottom: 22,
      left: 13,
      right: 14,
    });
  });
});
