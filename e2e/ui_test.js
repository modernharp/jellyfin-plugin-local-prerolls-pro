// Browser test of the settings page and real playback, against the server from e2e/setup.sh.
// Run: node e2e/ui_test.js   (needs Playwright with Chromium). Screenshots go to .jellyfin-test/screenshots/.
const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const BASE = `http://localhost:${process.env.JF_PORT || 8096}`;
const OUT = path.join(__dirname, '..', '.jellyfin-test', 'screenshots');
fs.mkdirSync(OUT, { recursive: true });
const failures = [];
function check(name, ok, detail) {
  console.log((ok ? 'PASS  ' : 'FAIL  ') + name + (ok ? '' : '   -> ' + detail));
  if (!ok) { failures.push(name); }
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error' && !/favicon|Failed to load resource/.test(m.text())) { errors.push('console: ' + m.text()); } });
  page.on('dialog', d => d.accept());
  const shot = name => page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: true });

  await page.goto(BASE + '/web/');
  await page.getByLabel('User').fill('admin', { timeout: 60000 });
  await page.getByLabel('Password').fill('admin');
  await page.getByRole('button', { name: 'Sign In' }).click();
  await page.waitForTimeout(4000);

  await page.goto(BASE + '/web/#/configurationpage?name=Local%20Pre-Rolls%20Pro');
  await page.waitForSelector('#liFilesBody tr', { timeout: 60000 });
  await page.waitForTimeout(1500);
  const rows = await page.locator('#liFilesBody > tr').count();
  check('Files tab lists pre-rolls', rows >= 6, rows);
  const filesText = await page.locator('#liFilesBody').innerText();
  check('Files tab shows tags, credit and conversion notes', /Rated PG/.test(filesText) && /By Test Studio/.test(filesText) && /converted/i.test(filesText), filesText.slice(0, 300));
  await shot('1-files');

  // Per-file editor: tag "neon" pre-roll as "night", turn it back on, keep, save.
  const neonRow = page.locator('#liFilesBody > tr', { hasText: 'neon-city.mp4' });
  await neonRow.getByRole('button', { name: 'Edit' }).click();
  await page.locator('#liFilesBody label', { hasText: 'Tags' }).locator('input').fill('neon, night');
  await page.locator('#liFilesBody label', { hasText: 'Plays' }).locator('select').selectOption('inherit');
  await shot('2-file-editor');
  await page.locator('#liFilesBody').getByRole('button', { name: 'Keep' }).click();
  await page.click('.li-savebar [data-save]');
  await page.waitForTimeout(4500);
  await page.click('.li-tab[data-tab=files]');
  await page.waitForTimeout(1500);
  const neonText = await page.locator('#liFilesBody > tr', { hasText: 'neon-city.mp4' }).first().innerText();
  check('Per-file edit is saved (tags shown)', /night/.test(neonText), neonText);

  // Rules tab: holiday preset picks up the "christmas" tag automatically.
  await page.click('.li-tab[data-tab=rules]');
  await page.selectOption('#liPreset', { label: 'Halloween (Oct 15–31)' });
  await page.waitForSelector('#liRuleEditor .li-card');
  await page.locator('#liRuleEditor label', { hasText: 'Every pre-roll tagged "horror"' }).locator('input').check();
  await page.click('#liRuleEditor >> text=Keep rule');
  await shot('3-rules');
  const rulesText = await page.locator('#liRulesBody').innerText();
  check('New rule targets a pre-roll tag', /tagged horror/.test(rulesText), rulesText.slice(0, 300));

  // Playback tab: opt-out search adds a title.
  await page.click('.li-tab[data-tab=playback]');
  await page.fill('#liOptOutSearch', 'Space');
  await page.waitForFunction(() => document.querySelectorAll('#liOptOutResults option').length > 1, null, { timeout: 15000 });
  await page.selectOption('#liOptOutResults', { index: 1 });
  const chips = await page.locator('#liOptOutChips').innerText();
  check('Opt-out list adds a title by search', /Space Odyssey/.test(chips), chips);
  await shot('4-playback');
  await page.click('.li-savebar [data-save]');
  await page.waitForTimeout(4000);

  // Catalog tab: grid with thumbnails, download one.
  await page.click('.li-tab[data-tab=catalog]');
  await page.waitForSelector('#liCatGrid .li-tile', { timeout: 60000 });
  await page.selectOption('#liCatPlatform', 'jellyfin');
  await page.check('#liCatDownloadable');
  await page.waitForTimeout(2500);
  const thumbsOk = await page.evaluate(async () => {
    const t = document.querySelector('#liCatGrid .li-thumb');
    const url = t && getComputedStyle(t).backgroundImage.replace(/^url\("?|"?\)$/g, '');
    if (!url) { return false; }
    const img = new Image(); img.src = url;
    await new Promise(r => { img.onload = r; img.onerror = r; });
    return img.naturalWidth > 0;
  });
  check('Catalog thumbnails load in the browser', thumbsOk, thumbsOk);
  const tile = page.locator('#liCatGrid .li-tile', { has: page.getByRole('button', { name: 'Download', exact: true }) }).first();
  const title = await tile.locator('strong').innerText();
  await tile.getByRole('button', { name: 'Download', exact: true }).click();
  await page.waitForFunction(t => [...document.querySelectorAll('#liCatGrid .li-tile')].some(x => x.innerText.includes(t) && x.innerText.includes('Downloaded ✓')), title, { timeout: 120000 });
  check('Catalog download from the page works', true);
  await shot('5-catalog');
  await page.uncheck('#liCatDownloadable');
  await page.waitForTimeout(2000);
  const linkCount = await page.locator('#liCatGrid a', { hasText: 'Open on prerolls.me' }).count();
  check('YouTube/Vimeo items show an "Open on prerolls.me" link', linkCount > 0, linkCount);

  // Test tab.
  await page.click('.li-tab[data-tab=test]');
  await page.fill('#liTestSearch', 'Kids');
  await page.waitForFunction(() => document.querySelector('#liTestItem') && document.querySelector('#liTestItem').value, null, { timeout: 15000 });
  await page.click('#liTestRun');
  await page.waitForSelector('#liTestResult .li-card');
  await page.waitForTimeout(500);
  const testText = await page.locator('#liTestResult').innerText();
  check('Test tab explains age-safe exclusions', /Left out before the rules ran/.test(testText) && /rated R/.test(testText), testText.slice(0, 400));
  await shot('6-test');

  // Backup tab shows the Local Intros import.
  await page.click('.li-tab[data-tab=backup]');
  const importInfo = await page.locator('#liImportInfo').innerText();
  check('Backup tab offers the Local Intros import', /Found Local Intros settings/.test(importInfo), importInfo);

  // Phone width.
  await page.click('.li-tab[data-tab=files]');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(800);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check('No sideways page scroll on a phone', overflow <= 2, overflow);
  await page.screenshot({ path: path.join(OUT, '7-phone.png') });
  await page.setViewportSize({ width: 1280, height: 800 });

  // Real playback in the web player: pre-show, then the movie.
  const kids = await page.evaluate(async () => (await ApiClient.getItems(ApiClient.getCurrentUserId(), { SearchTerm: 'Kids Fun', IncludeItemTypes: 'Movie', Recursive: true })).Items[0].Id);
  await page.goto(BASE + '/web/#/details?id=' + kids);
  await page.waitForTimeout(4000);
  await page.locator('.btnPlay, button[title="Play"]').first().click();
  const titles = [];
  for (let i = 0; i < 100; i++) { // Up to 150 s: downloaded pre-rolls can be 40 s long.
    await page.waitForTimeout(1500);
    const t = (await page.locator('.osdTitle, .pageTitle').first().innerText().catch(() => '')).trim();
    if (t && titles[titles.length - 1] !== t) { titles.push(t); }
    if (/Kids Fun/.test(t)) { break; }
  }
  console.log('      played in order: ' + titles.join(' → '));
  check('Web player plays the pre-show and then the movie', titles.length >= 2 && /Kids Fun/.test(titles[titles.length - 1]) && !/Kids Fun/.test(titles[0]), titles.join(' → '));
  await page.screenshot({ path: path.join(OUT, '8-playback.png') });

  check('No JavaScript errors on the page', errors.length === 0, errors.join(' | '));
  await browser.close();
  console.log(failures.length ? `\n${failures.length} failed` : '\nAll UI checks passed');
  process.exit(failures.length);
})().catch(e => { console.error(e); process.exit(99); });
