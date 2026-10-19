/**
 * Makes the reader's text size reach text the publisher sized absolutely.
 *
 * epub.js applies the text size as an inline `font-size: N%` on each contents
 * `<body>`. That only moves text whose size is relative to the body (`em`, `%`,
 * unset). Many converted books size their prose absolutely — `p { font-size:
 * 12pt }`, `.text { font-size: 1rem }`, `font-size: medium` — and that text
 * ignored the control entirely: the pill's percentage changed, the page did not.
 *
 * Every absolute `font-size` declaration in the section is rewritten once to
 * `calc(<authored> * var(--nostos-text-scale, 1))`, and the variable is set on
 * the section's root to the chosen size. Relative sizes are left alone because
 * the body percentage already scales them; scaling them as well would compound.
 */

export const TEXT_SCALE_VAR = '--nostos-text-scale';

const ABSOLUTE_LENGTH = /^\s*(-?(?:\d+\.?\d*|\.\d+))(px|pt|pc|in|cm|mm|q|rem)\s*$/i;

/** CSS absolute-size keywords at the browser default medium of 16px. */
const ABSOLUTE_KEYWORDS: Record<string, string> = {
  'xx-small': '9px',
  'x-small': '10px',
  small: '13px',
  medium: '16px',
  large: '18px',
  'x-large': '24px',
  'xx-large': '32px',
  'xxx-large': '48px',
};

/**
 * The scaled form of one authored `font-size` value, or null when the value is
 * already relative (or not something we can safely multiply). Pure.
 */
export function scaledFontSize(value: string): string | null {
  const keyword = ABSOLUTE_KEYWORDS[value.trim().toLowerCase()];
  const length = keyword ?? (ABSOLUTE_LENGTH.test(value) ? value.trim() : null);
  return length ? `calc(${length} * var(${TEXT_SCALE_VAR}, 1))` : null;
}

/**
 * A rule that sizes only the root. Its size is the base `rem` and the body
 * percentage resolve against, so scaling it too would apply the size twice.
 */
function targetsOnlyRoot(selectorText: string): boolean {
  return selectorText
    .split(',')
    .every((part) => /^\s*(html|:root)\s*$/i.test(part));
}

function scaleDeclaration(style: CSSStyleDeclaration): void {
  const scaled = scaledFontSize(style.getPropertyValue('font-size'));
  if (scaled) style.setProperty('font-size', scaled, style.getPropertyPriority('font-size'));
}

/**
 * Rules are told apart by shape, not `instanceof`: a section lives in its own
 * iframe, whose CSSOM classes are not the app window's.
 */
function scaleRules(rules: CSSRuleList, seen: Set<CSSStyleSheet>): void {
  for (const rule of Array.from(rules) as any[]) {
    if (typeof rule.selectorText === 'string' && rule.style) {
      if (!targetsOnlyRoot(rule.selectorText)) scaleDeclaration(rule.style);
    }
    if (rule.styleSheet) {
      // @import
      scaleSheet(rule.styleSheet, seen);
    }
    if (rule.cssRules) {
      // @media, @supports, @layer and nested style rules.
      scaleRules(rule.cssRules, seen);
    }
  }
}

function scaleSheet(sheet: CSSStyleSheet, seen: Set<CSSStyleSheet>): void {
  if (seen.has(sheet)) return;
  seen.add(sheet);
  let rules: CSSRuleList;
  try {
    rules = sheet.cssRules;
  } catch {
    // A cross-origin sheet cannot be read; it keeps its authored sizes.
    return;
  }
  scaleRules(rules, seen);
}

/**
 * Rewrites the section's absolute font sizes to follow {@link TEXT_SCALE_VAR}.
 * Idempotent: a rewritten value is a `calc()`, which is never rewritten again.
 */
export function scaleAbsoluteFontSizes(doc: Document): void {
  const seen = new Set<CSSStyleSheet>();
  for (const sheet of Array.from(doc.styleSheets)) scaleSheet(sheet, seen);

  for (const el of Array.from(doc.querySelectorAll<HTMLElement>('[style]'))) {
    if (el === doc.documentElement) continue;
    scaleDeclaration(el.style);
  }
}

/** Sets the multiplier the rewritten sizes read, from a size in percent. */
export function setTextScale(doc: Document, percent: number): void {
  doc.documentElement.style.setProperty(TEXT_SCALE_VAR, String(percent / 100));
}
