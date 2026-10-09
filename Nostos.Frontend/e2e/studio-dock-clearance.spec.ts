import { expect, test } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiPost, loadFixture } from './support/fixture';

async function undersizedVisibleControls(page: import('@playwright/test').Page): Promise<string[]> {
  return page.evaluate(() => {
    const root = document.querySelector('.studio-layout');
    if (!root) return ['missing Studio layout'];
    return Array.from(root.querySelectorAll(
      'button, select, textarea, input:not([type="hidden"]):not([type="checkbox"]), [role="tab"], a[appButton], a.source-badge',
    )).flatMap((element) => {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      if (!rect.width || !rect.height || style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0' || element.closest('[aria-hidden="true"]')) return [];
      if (rect.width >= 44 && rect.height >= 44) return [];
      const label = element.getAttribute('aria-label') || (element.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 50);
      return [`${label || element.tagName}: ${rect.width.toFixed(1)}x${rect.height.toFixed(1)}`];
    });
  });
}

const viewports = [
  { width: 1440, height: 900 },
  { width: 1280, height: 720 },
  { width: 1024, height: 768 },
  { width: 820, height: 1180 },
  { width: 390, height: 844 },
  { width: 844, height: 390 },
];

test('Studio Files rail scroll end clears the dock across the acceptance viewports', async ({
  browser,
}) => {
  const fixture = loadFixture();
  const stamp = Date.now().toString(36);
  const writingIds: string[] = [];

  for (let index = 0; index < 40; index++) {
    const writing = await apiPost<{ id: string }>(fixture.baseUrl, '/api/writings', {
      name: 'Dock file ' + stamp + ' ' + String(index + 1).padStart(2, '0'),
      type: 'Document',
      parentId: null,
    });
    writingIds.push(writing.id);
  }

  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    isMobile: true,
    hasTouch: true,
    deviceScaleFactor: 1,
  });
  const page = await context.newPage();

  try {
    await page.addInitScript(() => localStorage.setItem('nostos.theme', 'light'));
    await page.goto(fixture.baseUrl + '/studio?writingId=' + writingIds[0], {
      waitUntil: 'domcontentloaded',
    });
    await page.locator('.tox-tinymce').waitFor({ timeout: 45_000 });
    const filesToggle = page.locator('.files-toggle');
    if ((await filesToggle.getAttribute('aria-expanded')) !== 'true') {
      await filesToggle.click();
    }

    const fileList = page.locator('.file-list');
    const rows = fileList.locator('.tree-row');
    expect(await rows.count()).toBeGreaterThanOrEqual(40);

    const dockFailures: string[] = [];
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      if ((await filesToggle.getAttribute('aria-expanded')) !== 'true') {
        await filesToggle.click();
      }
      await fileList.evaluate((element) => {
        element.scrollTop = element.scrollHeight;
      });
      const scrollEnd = await page.evaluate(() => {
        const row = document
          .querySelector('.file-list .tree-row:last-child')!
          .getBoundingClientRect();
        const dock = document.querySelector('app-app-dock')!.getBoundingClientRect();
        return { rowBottom: row.bottom, dockTop: dock.top };
      });
      if (scrollEnd.rowBottom > scrollEnd.dockTop) {
        dockFailures.push(
          `Files final row at ${viewport.width}x${viewport.height}: ${scrollEnd.rowBottom}px > dock ${scrollEnd.dockTop}px`,
        );
      }

      if (viewport.width === 390 || viewport.width === 820) {
        const smallControls = await undersizedVisibleControls(page);
        if (smallControls.length) console.log(`[coarse-targets] Studio Files ${viewport.width}x${viewport.height}: ${JSON.stringify(smallControls)}`);
      }
      if (viewport.width === 820 || viewport.width === 390) {
        await page.screenshot({
          path: `/tmp/809-studio-files-scroll-end-after-${viewport.width}x${viewport.height}-light.png`,
          animations: 'disabled',
        });
      }
    }
    expect(dockFailures, 'Studio Files rail clears the dock across viewports').toEqual([]);
  } finally {
    await context.close();
    await Promise.all(writingIds.map((id) => apiDelete(fixture.baseUrl, '/api/writings/' + id)));
  }
});
