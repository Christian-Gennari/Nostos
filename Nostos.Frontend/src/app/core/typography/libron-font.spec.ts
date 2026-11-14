import { describe, expect, it } from 'vitest';

import { LIBRON_FACES, libronFaceUrls, libronFontFaceCss } from './libron-font';

const BASE = 'https://nostos.test/app/';

describe('Libron faces', () => {
  it('bundles the four faces that exist, and no more', () => {
    expect(LIBRON_FACES).toEqual([
      { file: 'Libron-Regular.woff2', style: 'normal', weight: 400 },
      { file: 'Libron-Italic.woff2', style: 'italic', weight: 400 },
      { file: 'Libron-Bold.woff2', style: 'normal', weight: 700 },
      { file: 'Libron-BoldItalic.woff2', style: 'italic', weight: 700 },
    ]);
  });

  it('resolves absolute URLs, because a reader section is its own document', () => {
    expect(libronFaceUrls(BASE)).toEqual([
      `${BASE}fonts/libron/Libron-Regular.woff2`,
      `${BASE}fonts/libron/Libron-Italic.woff2`,
      `${BASE}fonts/libron/Libron-Bold.woff2`,
      `${BASE}fonts/libron/Libron-BoldItalic.woff2`,
    ]);
  });

  it('declares every face in one CSS block', () => {
    const css = libronFontFaceCss(BASE);

    expect(css.match(/@font-face/g)).toHaveLength(4);
    expect(css).toContain('font-family:"Libron"');
    expect(css).toContain('font-style:normal;font-weight:400');
    expect(css).toContain('font-style:italic;font-weight:400');
    expect(css).toContain('font-style:normal;font-weight:700');
    expect(css).toContain('font-style:italic;font-weight:700');
    expect(css).toContain(`src:url("${BASE}fonts/libron/Libron-Regular.woff2") format("woff2")`);
    expect(css).toContain('font-display:block');
  });

  it('points at the deployment it is running in', () => {
    expect(libronFontFaceCss('http://localhost:5214/')).toContain(
      'http://localhost:5214/fonts/libron/Libron-Bold.woff2',
    );
  });
});
