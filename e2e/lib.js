// Shared helpers for the browser tests.
const { chromium } = require('playwright');
const BASE = `http://localhost:${process.env.JF_PORT || 8096}`;
const PLUGIN = 'd18a3ff6-9bbb-4218-9158-c9d2642eee86';

async function openPlugin(opts = {}) {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: opts.viewport || { width: 1280, height: 900 } });
  const log = { errors: [], dialogs: [] };
  page.on('pageerror', e => log.errors.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error' && !/favicon|Failed to load resource/.test(m.text())) { log.errors.push('console: ' + m.text()); } });
  page.on('dialog', d => { log.dialogs.push(d.message()); (opts.dismissDialogs ? d.dismiss() : d.accept()); });
  await page.goto(BASE + '/web/');
  await page.getByLabel('User').fill('admin', { timeout: 60000 });
  await page.getByLabel('Password').fill('admin');
  await page.getByRole('button', { name: 'Sign In' }).click();
  await page.waitForTimeout(3500);
  await page.goto(BASE + '/web/#/configurationpage?name=Local%20Pre-Rolls%20Pro');
  await page.waitForSelector('#liFilesBody tr', { timeout: 60000 });
  await page.waitForTimeout(1200);
  return { browser, page, log };
}

// Reads the saved plugin settings through the page's own login.
async function savedConfig(page) {
  return page.evaluate(id => ApiClient.getPluginConfiguration(id), PLUGIN);
}

// Text of Jellyfin's alert dialog if one is open, then closes it.
async function takeAlert(page) {
  const dlg = page.locator('.dialogContainer .dialog');
  if (!(await dlg.count())) { return null; }
  const text = (await dlg.first().innerText()).trim();
  const ok = dlg.getByRole('button', { name: /ok|got it|close/i });
  if (await ok.count()) { await ok.first().click(); } else { await page.keyboard.press('Escape'); }
  await page.waitForTimeout(400);
  return text;
}

module.exports = { BASE, PLUGIN, openPlugin, savedConfig, takeAlert };
