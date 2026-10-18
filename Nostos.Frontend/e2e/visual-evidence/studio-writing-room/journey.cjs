// Epic #1036 final journey: blank page → write → @ quote with author → passage
// sentences via @ → Studio theme + font → typewriter sounds → reload keeps all.
// node journey-final.cjs <frontendDir> <base> <api> <outDir> [WxH:theme]
const { createRequire } = require('module');
const req = createRequire(process.argv[2] + '/package.json');
const { chromium } = req('playwright');
const base = process.argv[3]; const api = process.argv[4]; const outDir = process.argv[5];
const [w, h, theme] = (process.argv[6] || '1440x900:light').split(/[x:]/);
const tag = `${w}-${theme}`;
const phone = +w < 700;
let failures = 0;
const log = (...a) => console.log('[final]', ...a.map((x) => (typeof x === 'object' ? JSON.stringify(x) : x)));
const check = (label, ok, detail) => { if (!ok) failures++; log(ok ? 'PASS' : 'FAIL', label, detail ?? ''); };
const shot = (page, step) => page.screenshot({ path: `${outDir}/${step}-${tag}.png` });
const title = `Journey ${tag} ${Date.now()}`;

(async () => {
  const browser = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] });
  const context = await browser.newContext({ viewport: { width: +w, height: +h }, hasTouch: phone });
  await context.addInitScript((t) => {
    if (!sessionStorage.getItem('journey-started')) {
      sessionStorage.setItem('journey-started', '1');
      localStorage.setItem('nostos.theme', t);
      localStorage.removeItem('nostos.studio.preferences');
      localStorage.removeItem('nostos.studio.lastWritingId');
    }
    window.__starts = 0;
    const orig = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (...a) { window.__starts++; return orig.apply(this, a); };
  }, theme);
  const page = await context.newPage();
  const errors = []; page.on('pageerror', (e) => errors.push(e.message));
  const editor = page.locator('app-markdown-editor .nostos-manuscript-body');
  const palette = page.getByTestId('source-palette');

  // 1. Blank page.
  await page.goto(`${base}/studio`);
  await editor.waitFor(); await page.waitForTimeout(600);
  const focused = await page.evaluate(() => !!document.activeElement?.closest('.nostos-manuscript-body'));
  check('opens on a blank page with the caret in it', focused && (await editor.innerText()).trim() === '');
  await shot(page, '1-blank-page');

  // 2. Write.
  await page.keyboard.type(`# ${title}`, { delay: 5 });
  await page.keyboard.press('Enter');
  await page.keyboard.type('What we want is never what we need, and the wanting itself is the trouble.', { delay: 5 });
  let writing = null;
  for (let i = 0; i < 40 && !writing; i++) {
    writing = (await (await fetch(`${api}/api/writings`)).json()).find((x) => x.name === title);
    if (!writing) await page.waitForTimeout(250);
  }
  check('first words create a writing named from its first line', !!writing);
  await shot(page, '2-writing');

  // 3. @schopenhauer will → quote with the author.
  await page.keyboard.press('Enter');
  await page.keyboard.type('@schopenhauer will', { delay: 20 });
  await palette.waitFor(); await page.waitForTimeout(700);
  const firstRow = (await palette.locator('.sp-row').first().innerText()).replace(/\s+/g, ' ');
  check('palette finds Schopenhauer on "will"', /Schopenhauer/.test(firstRow), firstRow.slice(0, 100));
  await shot(page, '3a-palette');
  await page.keyboard.press('Enter');
  await palette.waitFor({ state: 'detached' });
  const quote = editor.locator('blockquote').filter({ hasText: 'Arthur Schopenhauer' });
  const quoteText = (await quote.first().innerText().catch(() => '')).replace(/\s+/g, ' ');
  check('quote inserted with "— Arthur Schopenhauer, The World as Will…"', /— Arthur Schopenhauer, The World as Will/.test(quoteText), quoteText.slice(-90));
  await shot(page, '3b-quote');

  // 4. Open a passage through @ and quote two of its sentences.
  await page.keyboard.type('@vanity', { delay: 20 });
  await palette.waitFor(); await page.waitForTimeout(700);
  await page.keyboard.press('Enter');
  const viewer = page.getByTestId('passage-viewer-text');
  await viewer.waitFor();
  await viewer.evaluate((root) => {
    const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode; const at = node.data.indexOf('Time is that');
      if (at < 0) continue;
      const end = node.data.indexOf('they possess.') + 'they possess.'.length;
      const range = root.ownerDocument.createRange(); range.setStart(node, at); range.setEnd(node, end);
      const sel = root.ownerDocument.getSelection(); sel.removeAllRanges(); sel.addRange(range); return;
    }
    throw new Error('sentences not found');
  });
  await page.waitForTimeout(200);
  await shot(page, '4a-passage-open');
  await page.getByTestId('passage-viewer-quote').click();
  await page.waitForTimeout(150);
  check('the viewer confirms on its button, with no toast over the page',
    /Quoted/.test(await page.getByTestId('passage-viewer-quote').innerText()) && (await page.locator('.toast').count()) === 0);
  await shot(page, '4c-quote-confirmed');
  await page.getByTestId('passage-viewer-close').click();
  await page.waitForTimeout(400);
  const pq = (await editor.locator('blockquote').filter({ hasText: 'Time is that' }).first().innerText().catch(() => '')).replace(/\s+/g, ' ');
  check('two passage sentences quoted', pq.includes('Time is that') && pq.includes('they possess.'), pq.slice(0, 60));
  check('the whole passage was not inserted', !(await editor.innerText()).includes('This vanity finds expression'));
  await shot(page, '4b-passage-quoted');

  // 5. Theme and font. The theme is the app's (#1074), chosen from the page settings.
  await page.getByTitle(/^Page: theme/).click();
  const panel = page.getByTestId('page-settings');
  await panel.waitFor();
  await panel.locator('[data-testid="ps-theme"] [data-theme-option="sepia"]').click();
  await panel.locator('.ps-typeface').click();
  await panel.locator('[data-font-option="eb-garamond"]').click();
  await page.waitForTimeout(700);
  const face = () => editor.evaluate((el) => ({ font: getComputedStyle(el).fontFamily, themed: document.documentElement.getAttribute('data-palette') === 'sepia' }));
  const f1 = await face();
  check('Sepia theme and EB Garamond applied', f1.themed && /Garamond/.test(f1.font), f1);
  await shot(page, '5-theme-font');

  // 6. Typewriter sounds.
  await panel.getByRole('checkbox', { name: 'Typewriter sounds' }).click();
  await page.waitForTimeout(900);
  await shot(page, '6a-sounds-on');
  // A tap on the page above the sheet closes it on a phone; Esc on a desktop.
  if (phone) await page.mouse.click(30, 90);
  else await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
  await editor.locator('p').last().click();
  // ProseMirror reads a tapped caret on the next selectionchange; a script
  // that types in the same millisecond would beat it, a person never does.
  await page.waitForTimeout(300);
  await page.keyboard.press('Control+End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Written to the sound of keys.', { delay: 70 });
  await page.waitForTimeout(300);
  const starts = await page.evaluate(() => window.__starts);
  check('typing makes sounds', starts > 5, `${starts} sounds`);
  await shot(page, '6b-typing-with-sounds');

  // 7. Reload: the Markdown and the page settings are kept.
  let saved = '';
  for (let i = 0; i < 40; i++) {
    saved = (await (await fetch(`${api}/api/writings/${writing.id}`)).json()).content;
    if (saved.includes('sound of keys.')) break;
    await page.waitForTimeout(250);
  }
  await page.reload();
  await editor.waitFor(); await page.waitForTimeout(900);
  const after = await editor.innerText();
  saved = (await (await fetch(`${api}/api/writings/${writing.id}`)).json()).content;
  check('saved Markdown has the heading', saved.startsWith(`# ${title}\n`));
  check('saved Markdown has the quote and attribution', /\n> .+\n> ?\n> — Arthur Schopenhauer, [_*]The World as Will and Representation[_*]/.test(saved));
  check('saved Markdown has both passage sentences', /> Time is that[^\n]*they possess\./.test(saved));
  check('saved Markdown has the last line', saved.trimEnd().endsWith('Written to the sound of keys.'));
  check('reopened on the same writing with everything shown', after.includes(title) && after.includes('Arthur Schopenhauer') && after.includes('they possess.') && after.includes('sound of keys.'));
  const f2 = await face();
  check('theme and font kept after reload', f2.themed && /Garamond/.test(f2.font), f2);
  await page.getByTitle(/^Page: theme/).click();
  await panel.waitFor();
  check('sounds still on after reload', await panel.getByRole('checkbox', { name: 'Typewriter sounds' }).isChecked());
  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
  await shot(page, '7-after-reload');
  log('saved markdown:\n' + saved);
  check('no page errors', errors.length === 0, errors);
  await browser.close();
  log(failures ? `${failures} FAILED` : 'ALL PASSED');
  process.exit(failures ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
