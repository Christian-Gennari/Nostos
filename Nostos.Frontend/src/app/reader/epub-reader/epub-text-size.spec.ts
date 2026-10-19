import { scaleAbsoluteFontSizes, scaledFontSize, setTextScale } from './epub-text-size';

const SCALED = (v: string) => `calc(${v} * var(--nostos-text-scale, 1))`;

describe('scaledFontSize', () => {
  it('scales absolute lengths and keywords', () => {
    expect(scaledFontSize('12pt')).toBe(SCALED('12pt'));
    expect(scaledFontSize(' 1.2rem ')).toBe(SCALED('1.2rem'));
    expect(scaledFontSize('16PX')).toBe(SCALED('16PX'));
    expect(scaledFontSize('medium')).toBe(SCALED('16px'));
    expect(scaledFontSize('x-large')).toBe(SCALED('24px'));
  });

  it('leaves relative and already-scaled sizes to the body percentage', () => {
    for (const value of ['1em', '120%', 'larger', 'smaller', 'inherit', '', '2vw', SCALED('12pt')]) {
      expect(scaledFontSize(value)).toBeNull();
    }
  });
});

describe('scaleAbsoluteFontSizes', () => {
  /** A section document like epub.js renders: its own iframe, the book's CSS. */
  function section(css: string, body: string): { doc: Document; frame: HTMLIFrameElement } {
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const doc = frame.contentDocument!;
    doc.open();
    doc.write(`<html><head><style>${css}</style></head><body>${body}</body></html>`);
    doc.close();
    return { doc, frame };
  }

  /** The font-size each rule in the section's sheet now declares. */
  function declared(doc: Document): Record<string, string> {
    const out: Record<string, string> = {};
    const walk = (rules: CSSRuleList) => {
      for (const rule of Array.from(rules) as any[]) {
        // Values only: jsdom drops `!important` on setProperty, so priority
        // survival is checked in a real browser, not here.
        if (rule.selectorText) out[rule.selectorText] = rule.style.getPropertyValue('font-size');
        if (rule.cssRules) walk(rule.cssRules);
      }
    };
    walk(doc.styleSheets[0].cssRules);
    return out;
  }

  it('makes absolutely sized publisher text follow the text size', () => {
    const { doc, frame } = section(
      'html{font-size:16px}' +
        'p.pt{font-size:12pt}' +
        '.rem{font-size:1rem}' +
        '.kw{font-size:medium}' +
        '.imp{font-size:20px !important}' +
        '@media screen{.media{font-size:10px}}' +
        '.em{font-size:1em}' +
        '.pct{font-size:80%}',
      '<p style="font-size:14px" id="inline">f</p>',
    );
    try {
      scaleAbsoluteFontSizes(doc);
      // Running it again (every content hook) must not compound.
      scaleAbsoluteFontSizes(doc);

      expect(declared(doc)).toEqual({
        // The root is the base rem and the body percentage resolve against.
        html: '16px',
        'p.pt': SCALED('12pt'),
        '.rem': SCALED('1rem'),
        '.kw': SCALED('16px'),
        '.imp': SCALED('20px'),
        '.media': SCALED('10px'),
        // Relative sizes already follow the body percentage epub.js sets.
        '.em': '1em',
        '.pct': '80%',
      });
      expect(doc.getElementById('inline')!.style.fontSize).toBe(SCALED('14px'));
    } finally {
      frame.remove();
    }
  });

  it('sets the multiplier on the section root', () => {
    const { doc, frame } = section('', '');
    try {
      setTextScale(doc, 150);
      expect(doc.documentElement.style.getPropertyValue('--nostos-text-scale')).toBe('1.5');
    } finally {
      frame.remove();
    }
  });
});
