import { expect, test } from '@playwright/test';

import { apiDelete } from './support/brain-fixture';
import { apiPost, loadFixture } from './support/fixture';

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

      if (viewport.width === 820) {
        await page.screenshot({
          path: '/tmp/809-studio-files-scroll-end-after-820x1180-light.png',
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
