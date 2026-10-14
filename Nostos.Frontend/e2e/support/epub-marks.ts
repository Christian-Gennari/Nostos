import type { Page } from '@playwright/test';

/**
 * How many ranges an EPUB mark layer paints across the reader's sections.
 * Marks are painted by the text through the CSS Custom Highlight API
 * (`epub-text-marks.ts`), so they live in each section's highlight registry,
 * not as elements in the page.
 */
export function epubMarkCount(
  page: Page,
  layer: 'search' | 'landing' | 'read-aloud' | `pen-${string}`,
): Promise<number> {
  return page.evaluate((name) => {
    let count = 0;
    for (const frame of document.querySelectorAll<HTMLIFrameElement>('#epub-viewer iframe')) {
      const win = frame.contentWindow as (Window & { CSS: { highlights?: Map<string, Set<Range>> } }) | null;
      count += win?.CSS.highlights?.get(name)?.size ?? 0;
    }
    return count;
  }, `nostos-mark-${layer}`);
}
