import { placeMenuAtAnchor } from './menu-placement';

/** A 1280×800 viewport with a 56px header and a 56px toolbar around the page. */
const viewport = { width: 1280, height: 800 };
const area = { top: 56, bottom: 744 };

describe('placeMenuAtAnchor (#650, #1829)', () => {
  it('opens below a selection with room under it, capped to the reading area', () => {
    const place = placeMenuAtAnchor({ top: 120, bottom: 160, left: 400, right: 600 }, viewport, area);
    expect(place.top).toBe(168);
    expect(place.bottom).toBeNull();
    // Stops 8px above the toolbar, never under it.
    expect(place.top! + place.maxHeight!).toBe(744 - 8);
  });

  it('opens above a selection near the foot, never above the header', () => {
    const place = placeMenuAtAnchor({ top: 600, bottom: 720, left: 400, right: 600 }, viewport, area);
    expect(place.top).toBeNull();
    expect(place.bottom).toBe(800 - 592);
    const menuTop = 800 - place.bottom! - place.maxHeight!;
    expect(menuTop).toBe(56 + 8);
  });

  it('never opens up into the header for a long selection from just under it (the report)', () => {
    // 118px above the text in the viewport, but only 46px below the header.
    // The old rule counted the header as room, opened above, and the menu sat
    // behind the header. Neither side has room, so it floats over the text.
    const place = placeMenuAtAnchor({ top: 118, bottom: 690, left: 400, right: 600 }, viewport, area);
    expect(place.top).toBeNull();
    expect(place.bottom).toBe(800 - 736);
    expect(800 - place.bottom! - place.maxHeight!).toBe(56 + 8);
  });

  it('opens below when the room under the text beats the room under the header', () => {
    const place = placeMenuAtAnchor({ top: 118, bottom: 560, left: 400, right: 600 }, viewport, area);
    expect(place.top).toBe(568);
    expect(place.bottom).toBeNull();
    expect(place.maxHeight).toBe(736 - 568);
  });

  it('floats over the text inside the reading area when the selection fills the page', () => {
    const place = placeMenuAtAnchor({ top: 70, bottom: 730, left: 100, right: 1180 }, viewport, area);
    expect(place.top).toBeNull();
    expect(place.bottom).toBe(800 - 736);
    expect(800 - place.bottom! - place.maxHeight!).toBe(64);
  });

  it('clamps the menu horizontally to the viewport', () => {
    const place = placeMenuAtAnchor({ top: 120, bottom: 160, left: 1250, right: 1270 }, viewport, area, 396);
    expect(place.width).toBe(396);
    expect(place.left + place.width).toBe(1280 - 8);
  });

  it('uses the viewport when the reading area reaches past it', () => {
    const place = placeMenuAtAnchor(
      { top: 40, bottom: 60, left: 400, right: 600 },
      viewport,
      { top: -100, bottom: 2000 },
    );
    expect(place.top).toBe(68);
    expect(place.maxHeight).toBe(792 - 68);
  });
});
