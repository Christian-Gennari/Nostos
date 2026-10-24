/**
 * The one phone breakpoint. Stylesheets write `@media (max-width: 768px)`
 * (CSS cannot read a constant); TypeScript reads it from here, so the two never
 * drift. The design check flags other phone widths in overlay stylesheets.
 */
export const PHONE_MAX = 768;

/** A phone-width viewport: overlays become sheets, drawers hide the dock. */
export const PHONE_QUERY = `(max-width: ${PHONE_MAX}px)`;

/** A phone-width viewport or a touch-first device (tablets included). */
export const PHONE_OR_TOUCH_QUERY = `${PHONE_QUERY}, (pointer: coarse)`;

/** Whether the viewport is phone-wide right now (false where matchMedia is missing). */
export function isPhoneViewport(): boolean {
  if (typeof window === 'undefined') return false;
  if (typeof window.matchMedia === 'function') return window.matchMedia(PHONE_QUERY).matches;
  return window.innerWidth <= PHONE_MAX;
}
