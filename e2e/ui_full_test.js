// Full click-through test of the settings page in Chromium, against a fresh server from e2e/setup.sh.
// It uses every tab and control like a person would, enters bad values on purpose, and checks
// three things each time: what the page shows, what was saved, and what really happens
// (via the plugin's Test results and Jellyfin's own intro API).
//   node e2e/ui_full_test.js        screenshots go to .jellyfin-test/screenshots/full/
const path = require('path');
const fs = require('fs');
const { openPlugin, savedConfig, takeAlert } = require('./lib.js');

const ROOT = path.join(__dirname, '..');
const MEDIA = path.join(ROOT, '.jellyfin-test', 'media');
const OUT = path.join(ROOT, '.jellyfin-test', 'screenshots', 'full');
fs.mkdirSync(OUT, { recursive: true });

const results = [];
let page, log;

function expect(cond, message) { if (!cond) { throw new Error(message); } }
async function step(name, fn) {
  try {
    await fn();
    results.push([true, name]);
    console.log('PASS  ' + name);
  } catch (e) {
    results.push([false, name]);
    console.log('FAIL  ' + name + '\n        ' + String(e.message || e).split('\n')[0]);
    await page.screenshot({ path: path.join(OUT, 'fail-' + results.length + '.png'), fullPage: true }).catch(() => {});
    await takeAlert(page).catch(() => {});
  }
}
const tab = async t => { await page.click(`.li-tab[data-tab=${t}]`); await page.waitForTimeout(400); };
const shot = n => page.screenshot({ path: path.join(OUT, n + '.png'), fullPage: true });
const text = async sel => (await page.locator(sel).first().innerText()).trim();
const errorsBox = () => text('#liErrors').catch(() => '');

// Clicks Save and waits until the page says the changes are saved (or the error box appears).
async function save() {
  await page.locator('.li-savebar [data-save]').click();
  await page.waitForFunction(() => document.getElementById('liDirty').textContent === '' || document.querySelector('#liErrors .li-errorbox'), null, { timeout: 15000 });
  await page.waitForTimeout(600);
  await takeAlert(page);
  return errorsBox();
}
async function saveOk() {
  const err = await save();
  expect(!err, 'save was blocked: ' + err);
  await page.waitForTimeout(2500); // the server rescans ~2 s after a save
}
async function saveBlocked(expected) {
  const err = await save();
  expect(err.includes('Not saved'), 'save was not blocked');
  expect(err.includes(expected), `error box should mention "${expected}" but says: ${err}`);
}
async function itemId(name) {
  return page.evaluate(async n => (await ApiClient.getItems(ApiClient.getCurrentUserId(), { SearchTerm: n, IncludeItemTypes: 'Movie,Episode', Recursive: true })).Items[0].Id, name);
}
async function simulate(name, at, noUser) {
  const id = await itemId(name);
  return page.evaluate(async ([id, at, noUser]) => ApiClient.getJSON(ApiClient.getUrl('LocalPrerollsPro/Simulate', { itemId: id, userId: noUser ? undefined : ApiClient.getCurrentUserId(), at: at || undefined })), [id, at, noUser]);
}
const picks = s => (s.Intros || []).map(i => i.Name);
async function realIntros(name) {
  const id = await itemId(name);
  return page.evaluate(async id => (await ApiClient.getJSON(ApiClient.getUrl('Users/' + ApiClient.getCurrentUserId() + '/Items/' + id + '/Intros'))).Items.map(i => i.Name), id);
}
async function status() { return page.evaluate(() => ApiClient.getJSON(ApiClient.getUrl('LocalPrerollsPro/Status'))); }
const row = key => page.locator(`#liFilesBody tr[data-key="${key}"]`);
const pickBox = (scope, label) => page.locator(scope + ' .li-pick label', { hasText: label }).locator('input');

(async () => {
  const opened = await openPlugin();
  page = opened.page; log = opened.log;
  const prerollDir = path.join(MEDIA, 'prerolls');

  // ================= FILES =================
  await step('Files: empty state explains what to do', async () => {
    expect((await text('#liFilesBody')).includes('No pre-rolls yet'), 'empty table message missing');
    await page.waitForTimeout(700);
    expect((await text('#liFolderChecks')).includes('Add at least one folder'), 'folder hint missing');
    await shot('01-files-empty');
  });
  await step('Files: a relative path is flagged live and blocks Save', async () => {
    await page.fill('#liFolders', 'media/prerolls');
    await page.waitForTimeout(900);
    expect((await text('#liFolderChecks')).includes('Use a full path'), 'live check did not flag relative path');
    await saveBlocked('not a full path');
    expect(await page.locator('.li-tab[data-tab=files] .li-dot').count() === 1, 'Files tab has no red dot');
    expect((await savedConfig(page)).IntroFolders.length === 0, 'bad folder was saved anyway');
  });
  await step('Files: a missing folder is reported as not found', async () => {
    await page.fill('#liFolders', '/does/not/exist');
    await page.waitForTimeout(900);
    expect((await text('#liFolderChecks')).includes('Not found on the server'), await text('#liFolderChecks'));
  });
  await step('Files: a real folder shows how many videos it has', async () => {
    await page.fill('#liFolders', prerollDir);
    await page.waitForTimeout(900);
    expect((await text('#liFolderChecks')).includes('6 videos'), await text('#liFolderChecks'));
  });
  await step('Files: file types are tidied, and an empty list blocks Save', async () => {
    await page.fill('#liExtensions', 'mp4 ;; mkv ,MOV');
    await page.locator('#liExtensions').blur();
    expect(await page.inputValue('#liExtensions') === '.mp4, .mkv, .mov', 'not normalized: ' + await page.inputValue('#liExtensions'));
    await page.fill('#liExtensions', ';;');
    await page.locator('#liExtensions').blur();
    await saveBlocked('no valid video file types');
    await page.fill('#liExtensions', '.mp4, .mkv');
    await page.locator('#liExtensions').blur();
  });
  await step('Files: "Include subfolders" off skips the holidays/ subfolder', async () => {
    await page.uncheck('#liSubfolders');
    await page.click('#liScanBtn');
    await page.waitForTimeout(3500);
    expect((await text('#liFilesTitle')) === 'Pre-rolls (5)', await text('#liFilesTitle'));
    expect((await savedConfig(page)).ScanSubfolders === false, 'not saved');
  });
  await step('Files: Save and scan with subfolders finds all 6 pre-rolls', async () => {
    await page.check('#liSubfolders');
    await page.click('#liScanBtn');
    await page.waitForTimeout(3500);
    expect((await text('#liFilesTitle')) === 'Pre-rolls (6)', await text('#liFilesTitle'));
    expect((await text('#liScanStatus')).includes('6 pre-rolls found'), await text('#liScanStatus'));
    const c = await savedConfig(page);
    expect(c.IntroFolders[0] === prerollDir && c.VideoExtensions === '.mp4,.mkv', JSON.stringify([c.IntroFolders, c.VideoExtensions]));
    await shot('02-files');
  });
  await step('Files: codecs and loudness appear after the background check', async () => {
    await page.waitForFunction(() => !document.querySelector('#liFilesBody').innerText.includes('checking…'), null, { timeout: 120000 });
    await tab('rules'); await tab('files'); await page.waitForTimeout(1200);
    const t = await text('#liFilesBody');
    expect(/LUFS/.test(t) && /hevc/.test(t), 'no loudness/codec info');
    expect(/Converted copy is used/.test(t), 'no conversion note for the HEVC file');
    expect(/louder than the target/.test(t), 'no loudness warning for too-loud.mp4');
  });
  await step('Files: Edit rejects a bad weight and an impossible date range', async () => {
    await row('neon-city.mp4').getByRole('button', { name: 'Edit' }).click();
    const ed = page.locator('tr.li-fileeditor');
    await ed.locator('label', { hasText: 'Weight' }).locator('input').fill('500');
    await ed.locator('label', { hasText: 'When' }).locator('select').selectOption('once');
    await ed.locator('label', { hasText: 'From' }).locator('input').fill('2026-12-10');
    await ed.locator('label', { hasText: /^To/ }).locator('input').fill('2026-12-01');
    await ed.getByRole('button', { name: 'Keep' }).click();
    const err = await ed.locator('.li-editorerror').innerText();
    expect(err.includes('Weight must be') && err.includes('start date is after the end date'), err);
    await shot('03-file-editor-errors');
  });
  await step('Files: Edit saves name, tags, rating, weight and turns the pre-roll off', async () => {
    const ed = page.locator('tr.li-fileeditor');
    await ed.locator('label', { hasText: 'Display name' }).locator('input').fill('Neon Nights');
    await ed.locator('label', { hasText: 'Tags' }).locator('input').fill('neon, night, neon');
    await ed.locator('label', { hasText: 'Rating' }).locator('select').selectOption('PG-13');
    await ed.locator('label', { hasText: 'Weight' }).locator('input').fill('2');
    await ed.locator('label', { hasText: 'When' }).locator('select').selectOption('none');
    await ed.locator('label', { hasText: 'Plays' }).locator('select').selectOption('false');
    await ed.getByRole('button', { name: 'Keep' }).click();
    expect(await page.locator('#liUnsaved').isVisible(), 'unsaved bar not shown');
    await saveOk();
    const fsn = (await savedConfig(page)).FileSettings.find(f => f.Key === 'neon-city.mp4');
    expect(fsn && fsn.Name === 'Neon Nights' && fsn.Tags.join() === 'neon,night' && fsn.Rating === 'PG-13' && fsn.Weight === 2 && fsn.Enabled === false, JSON.stringify(fsn));
    await tab('rules'); await tab('files'); await page.waitForTimeout(1200);
    const t = await row('neon-city.mp4').innerText();
    expect(t.includes('Neon Nights') && t.includes('off') && t.includes('Rated PG-13') && t.includes('Weight 2'), t);
    const s = await simulate('Space Odyssey');
    expect((s.Excluded || []).some(e => e.Name === 'Neon Nights' && e.Reason === 'turned off'), 'Test result does not show it turned off');
  });
  await step('Files: "Reset to defaults" brings the pre-roll back', async () => {
    await row('neon-city.mp4').getByRole('button', { name: 'Edit' }).click();
    await page.locator('tr.li-fileeditor').getByRole('button', { name: 'Reset to defaults' }).click();
    await saveOk();
    expect(!(await savedConfig(page)).FileSettings.some(f => f.Key === 'neon-city.mp4'), 'settings not removed');
    const s = await simulate('Space Odyssey');
    expect(!(s.Excluded || []).some(e => e.Reason === 'turned off'), 'still turned off');
  });
  await step('Files: an open editor blocks Save with a clear message', async () => {
    await row('scary.mp4').getByRole('button', { name: 'Edit' }).click();
    await saveBlocked('pre-roll editor is still open');
    await page.locator('tr.li-fileeditor').getByRole('button', { name: 'Cancel' }).click();
    await page.locator('#liErrors').evaluate(n => { n.innerHTML = ''; });
  });
  await step('Files: Preview opens a player', async () => {
    await row('retro_cinema.mp4').getByRole('button', { name: 'Preview' }).click();
    await page.waitForSelector('tr.li-preview video, tr.li-preview .li-warn', { timeout: 10000 });
    await row('retro_cinema.mp4').getByRole('button', { name: 'Preview' }).click();
    expect(await page.locator('tr.li-preview').count() === 0, 'preview did not close');
  });
  await step('Files: Re-check files, Reset play counts and Repair work', async () => {
    await page.click('#liPrepareBtn');
    await page.waitForTimeout(800);
    await page.click('#liResetStats');
    await page.waitForTimeout(800);
    await page.click('#liRebuildBtn');
    await page.waitForTimeout(3000);
    const a = await takeAlert(page);
    expect(a && a.includes('Library items repaired'), 'no confirmation: ' + a);
    expect((await text('#liFilesTitle')) === 'Pre-rolls (6)', 'files lost after repair');
    expect((await realIntros('Space Odyssey')).length === 1, 'pre-rolls do not play after repair');
  });

  // ================= RULES =================
  await tab('rules');
  await step('Rules: the default catch-all rule is there', async () => {
    const t = await text('#liRulesBody');
    expect(t.includes('All pre-rolls') && t.includes('Always'), t);
  });
  await step('Rules: an empty new rule cannot be kept', async () => {
    await page.click('#liAddRule');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    expect((await text('#liRuleEditor .li-editorerror')).includes('Tick at least one pre-roll'), await text('#liRuleEditor .li-editorerror'));
  });
  const addCond = kind => page.locator('#liRuleEditor select[aria-label="Add a condition"]').selectOption(kind);
  const cond = n => page.locator('#liRuleEditor .li-cond').nth(n);
  await step('Rules: incomplete conditions get specific messages', async () => {
    await addCond('Genre');
    await addCond('CurrentDate');
    await addCond('ProductionYear');
    await addCond('TimeOfDay');
    await cond(1).locator('select[aria-label="From month"]').selectOption('02');
    await cond(1).locator('input[aria-label="From day"]').fill('30');
    await cond(2).locator('label', { hasText: 'From' }).locator('input').fill('2025');
    await cond(2).locator('label', { hasText: 'To' }).locator('input').fill('2020');
    await page.locator('#liRuleEditor input[aria-label=Priority]').fill('5000');
    await page.locator('#liRuleEditor input[aria-label=Weight]').fill('0');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    const e = await text('#liRuleEditor .li-editorerror');
    for (const s of ['choose at least a genre', "Feb doesn't have that many days", 'first year is after the last year', 'fill in at least one of From and To', 'Priority must be', 'Weight must be']) {
      expect(e.includes(s), `missing "${s}" in: ${e}`);
    }
    await shot('04-rule-errors');
  });
  await step('Rules: chips add, warn about unknown names, and remove', async () => {
    const input = cond(0).locator('input[type=text]');
    await input.fill('Horror'); await input.press('Enter');
    await input.fill('Hororr'); await input.press('Enter');
    expect((await cond(0).innerText()).includes('no title in your library has "Hororr"'), 'no spelling hint');
    await input.fill('horror'); await input.press('Enter');
    expect((await cond(0).innerText()).includes('already chosen'), 'no duplicate hint');
    await cond(0).getByRole('button', { name: 'Remove Hororr' }).click();
    const chips = await cond(0).locator('.li-chip').allInnerTexts();
    expect(chips.length === 1 && chips[0].startsWith('Horror'), JSON.stringify(chips));
  });
  await step('Rules: a complete rule is kept and shown in plain words', async () => {
    await cond(3).getByRole('button', { name: 'Remove' }).click(); // time of day
    await cond(1).getByRole('button', { name: 'Remove' }).click(); // date
    await cond(1).locator('label', { hasText: 'From' }).locator('input').fill('2000');
    await cond(1).locator('label', { hasText: 'To' }).locator('input').fill('');
    await page.locator('#liRuleEditor input[aria-label=Priority]').fill('5');
    await page.locator('#liRuleEditor input[aria-label=Weight]').fill('1');
    await page.locator('#liRuleEditor input[placeholder="e.g. Horror at night"]').fill('Spooky');
    await pickBox('#liRuleEditor', 'Every pre-roll tagged "horror"').check();
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    const t = await page.locator('#liRulesBody tr[data-rule="Spooky"]').innerText();
    expect(t.includes('Genre is Horror, and Release year of the title 2000–any') && t.includes('tagged horror') && t.includes('5'), t);
  });
  await step('Rules: duplicate names are refused', async () => {
    await page.click('#liAddRule');
    await page.locator('#liRuleEditor input[placeholder="e.g. Horror at night"]').fill('spooky');
    await pickBox('#liRuleEditor', 'All pre-rolls').check();
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    expect((await text('#liRuleEditor .li-editorerror')).includes('already called'), 'duplicate allowed');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Cancel' }).click();
  });
  await step('Rules: on/off checkbox, Copy (cancel), Edit and Delete work', async () => {
    const r = page.locator('#liRulesBody tr[data-rule="Spooky"]');
    await r.locator('input[type=checkbox]').uncheck();
    expect(await page.locator('#liRulesBody tr[data-rule="Spooky"].li-off').count() === 1, 'not dimmed when off');
    await page.locator('#liRulesBody tr[data-rule="Spooky"] input[type=checkbox]').check();
    await page.locator('#liRulesBody tr[data-rule="Spooky"]').getByRole('button', { name: 'Copy' }).click();
    expect((await page.inputValue('#liRuleEditor input[placeholder="e.g. Horror at night"]')) === 'Spooky (copy)', 'copy name wrong');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    expect(await page.locator('#liRulesBody tr[data-rule="Spooky (copy)"]').count() === 1, 'copy not added');
    await page.locator('#liRulesBody tr[data-rule="Spooky (copy)"]').getByRole('button', { name: 'Edit' }).click();
    await page.locator('#liRuleEditor select').filter({ hasText: 'all of these are true' }).selectOption('Any');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    expect((await page.locator('#liRulesBody tr[data-rule="Spooky (copy)"]').innerText()).includes(', or '), 'match mode not applied');
    await page.locator('#liRulesBody tr[data-rule="Spooky (copy)"]').getByRole('button', { name: 'Delete' }).click();
    expect(await page.locator('#liRulesBody tr[data-rule="Spooky (copy)"]').count() === 0, 'not deleted');
  });
  await step('Rules: holiday preset fills dates and ticks the matching tag', async () => {
    await page.selectOption('#liPreset', { label: 'Christmas (Dec 1–26)' });
    expect(await page.inputValue('#liRuleEditor input[placeholder="e.g. Horror at night"]') === 'Christmas', 'name not set');
    expect(await page.locator('#liRuleEditor select[aria-label="From month"]').inputValue() === '12', 'month not set');
    expect(await pickBox('#liRuleEditor', 'Every pre-roll tagged "christmas"').isChecked(), 'tag not ticked');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Keep rule' }).click();
    await shot('05-rules');
  });
  await step('Rules: an open rule editor blocks Save', async () => {
    await page.click('#liAddRule');
    await saveBlocked('rule editor is still open');
    await page.locator('#liRuleEditor').getByRole('button', { name: 'Cancel' }).click();
  });
  await step('Rules: saved rules take effect', async () => {
    await saveOk();
    const rules = (await savedConfig(page)).Rules.map(r => r.Name);
    expect(rules.join('|') === 'All pre-rolls|Spooky|Christmas', rules.join('|'));
    const xmas = await simulate('Space Odyssey', '2026-12-24T20:00');
    expect(picks(xmas).join() === 'xmas', 'Christmas rule did not pick xmas: ' + picks(xmas));
    const nov = await simulate('Space Odyssey', '2026-11-08T20:00');
    expect(!picks(nov).includes('xmas'), 'xmas played outside its dates');
  });

  // ================= PRE-SHOW =================
  await tab('preshow');
  const stepCard = n => page.locator('#liSteps > .li-card').nth(n);
  await step('Pre-show: a Fixed step with nothing ticked blocks Save', async () => {
    await page.click('[data-addstep=Fixed]');
    await saveBlocked('has no pre-rolls ticked');
    await stepCard(1).locator('.li-pick label', { hasText: 'neon city' }).locator('input').check();
  });
  await step('Pre-show: counts outside 1–10 are refused', async () => {
    await page.click('[data-addstep=Trailers]');
    await stepCard(2).locator('input[type=number]').fill('11');
    expect((await stepCard(2).innerText()).includes('Must be from 1 to 10'), 'no inline error');
    await saveBlocked('needs a number from 1 to 10');
    await stepCard(2).locator('input[type=number]').fill('1');
    await stepCard(2).locator('label', { hasText: 'Prefer trailers' }).locator('input').uncheck();
  });
  await step('Pre-show: a step must apply to at least one kind of video', async () => {
    await stepCard(2).locator('input[data-applyto=Movie]').click();
    const a = await takeAlert(page);
    expect(a && a.includes('at least one kind of video'), 'no message: ' + a);
    expect(await stepCard(2).locator('input[data-applyto=Movie]').isChecked(), 'checkbox not restored');
  });
  await step('Pre-show: move steps up and down', async () => {
    await stepCard(1).getByRole('button', { name: 'Move step up' }).click();
    expect((await stepCard(0).innerText()).startsWith('1. Fixed'), await stepCard(0).innerText());
    await stepCard(0).getByRole('button', { name: 'Move step down' }).click();
    expect((await stepCard(0).innerText()).startsWith('1. Rule picks'), await stepCard(0).innerText());
  });
  await step('Pre-show: all steps off is refused', async () => {
    for (let i = 0; i < 3; i++) { await stepCard(i).locator('label', { hasText: 'On' }).first().locator('input').uncheck(); }
    await saveBlocked('every pre-show step is turned off');
    for (let i = 0; i < 3; i++) { await stepCard(i).locator('label', { hasText: 'On' }).first().locator('input').check(); }
  });
  await step('Pre-show: saved order plays rule pick, fixed bumper, then a trailer', async () => {
    await stepCard(0).locator('input[type=number]').fill('1');
    await saveOk();
    const c = await savedConfig(page);
    expect(c.PreShow.map(s => s.Kind).join() === 'Rules,Fixed,Trailers' && c.PreShow[2].SameGenre === false, JSON.stringify(c.PreShow.map(s => [s.Kind, s.SameGenre])));
    const s = await simulate('Kids Fun');
    const src = s.Intros.map(i => i.Source).join();
    expect(src === 'Rules,Fixed,Trailers' && s.Intros[2].Name === 'Trailer: Funny Day', src + ' / ' + picks(s));
    await shot('06-preshow');
  });
  await step('Pre-show: Remove deletes a step', async () => {
    await stepCard(2).getByRole('button', { name: 'Remove' }).click();
    await saveOk();
    expect((await savedConfig(page)).PreShow.length === 2, 'not removed');
  });

  // ================= PLAYBACK =================
  await tab('playback');
  await step('Playback: unticking every video type is refused', async () => {
    for (const t of ['Movie', 'Episode']) { await page.uncheck(`#liItemTypes input[data-type=${t}]`); }
    await saveBlocked('tick at least one kind of video');
    for (const t of ['Movie', 'Episode']) { await page.check(`#liItemTypes input[data-type=${t}]`); }
  });
  await step('Playback: every number field rejects out-of-range values', async () => {
    const bad = { '#liAvoidRepeat': '30', '#liBoostDays': '400', '#liBoostFactor': '0.5', '#liMinMinutes': '-1', '#liTargetLufs': '0' };
    for (const [sel, v] of Object.entries(bad)) { await page.fill(sel, v); }
    await page.selectOption('#liEpisodeMode', 'FirstOfBinge');
    expect(await page.locator('#liBingeGapWrap').isVisible(), 'binge field hidden');
    await page.fill('#liBingeGap', '0');
    const err = await save();
    for (const s of ['Avoid repeats', 'Boost days', 'Boost amount', 'Minutes between pre-shows', 'Target loudness', 'Binge break']) { expect(err.includes(s), `no error for ${s}: ${err}`); }
    expect(await page.locator('#liPlaybackX, .li-panel[data-panel=playback] .li-fielderror').count() >= 6, 'inline errors missing');
    await shot('07-playback-errors');
    const good = { '#liAvoidRepeat': '2', '#liBoostDays': '7', '#liBoostFactor': '2', '#liMinMinutes': '0', '#liTargetLufs': '-24', '#liBingeGap': '90' };
    for (const [sel, v] of Object.entries(good)) { await page.fill(sel, v); }
  });
  await step('Playback: "Show me" in the error box jumps to the field', async () => {
    await page.fill('#liAvoidRepeat', '99');
    await save();
    await tab('files');
    await page.locator('#liErrors').getByRole('button', { name: 'Show me' }).first().click();
    expect(await page.locator('.li-panel[data-panel=playback]').isVisible(), 'did not switch tab');
    expect(await page.evaluate(() => document.activeElement.id) === 'liAvoidRepeat', 'focus not on field');
    await page.fill('#liAvoidRepeat', '2');
  });
  await step('Playback: per-title folder name must be a plain name', async () => {
    await page.fill('#liTitleFolder', '../etc');
    expect((await page.locator('.li-panel[data-panel=playback]').innerText()).includes('plain folder name'), 'no inline error');
    await saveBlocked('per-title folder name');
    await page.fill('#liTitleFolder', '.prerolls');
  });
  await step('Playback: switches disable the fields they control', async () => {
    await page.uncheck('#liAgeSafe');
    expect(await page.isDisabled('#liAgeUnrated'), 'unrated select still enabled');
    await page.check('#liAgeSafe');
    await page.uncheck('#liTitleEnabled');
    expect(await page.isDisabled('#liTitleFolder') && await page.isDisabled('#liTitleMode'), 'title fields still enabled');
    await page.check('#liTitleEnabled');
    await page.selectOption('#liEpisodeMode', 'EveryEpisode');
    expect(!(await page.locator('#liBingeGapWrap').isVisible()), 'binge field still visible');
  });
  await step('Playback: no-pre-roll list adds by search, refuses duplicates, removes', async () => {
    await page.fill('#liOptOutSearch', 'Funny');
    await page.waitForFunction(() => document.querySelectorAll('#liOptOutResults option').length > 1, null, { timeout: 10000 });
    await page.selectOption('#liOptOutResults', { index: 1 });
    await page.fill('#liOptOutSearch', 'Funny');
    await page.waitForFunction(() => document.querySelectorAll('#liOptOutResults option').length > 1, null, { timeout: 10000 });
    await page.selectOption('#liOptOutResults', { index: 1 });
    const a = await takeAlert(page);
    expect(a && a.includes('already on the list'), 'no duplicate message: ' + a);
    await page.fill('#liOptOutSearch', 'Space');
    await page.waitForFunction(() => [...document.querySelectorAll('#liOptOutResults option')].some(o => o.textContent.includes('Space')), null, { timeout: 10000 });
    await page.selectOption('#liOptOutResults', { index: 1 });
    await page.getByRole('button', { name: /Remove Space Odyssey/ }).click();
    const chips = await text('#liOptOutChips');
    expect(chips.includes('Funny Day') && !chips.includes('Space'), chips);
    await page.fill('#liOptOutSearch', 'qqqq');
    await page.waitForTimeout(900);
    expect((await text('#liOptOutResults')).includes('Nothing found'), 'no empty-result message');
  });
  await step('Playback: every option is saved and takes effect', async () => {
    await page.selectOption('#liSelectionMode', 'LeastPlayed');
    await page.selectOption('#liAgeUnrated', 'true');
    await page.selectOption('#liTitleMode', 'AddFirst');
    await page.fill('#liOptOutTag', 'no-preroll');
    await page.uncheck('#liConvert'); await page.check('#liConvert');
    await page.check('#liLevel');
    await page.check('#liExcludedUsers input[data-user=admin]');
    await saveOk();
    const c = await savedConfig(page);
    const want = { SelectionMode: 'LeastPlayed', AvoidRepeatCount: 2, BoostNewDays: 7, BoostNewFactor: 2, AgeSafe: true, AgeSafeUnratedAllowAll: true, TitleIntrosEnabled: true, TitleFolderName: '.prerolls', TitleIntrosMode: 'AddFirst', OptOutTag: 'no-preroll', MinMinutesBetweenIntros: 0, EpisodeMode: 'EveryEpisode', BingeGapMinutes: 90, ConvertProblemFiles: true, LevelLoudness: true, TargetLoudness: -24 };
    for (const [k, v] of Object.entries(want)) { expect(c[k] === v, `${k} saved as ${c[k]}, expected ${v}`); }
    expect(c.OptOutItemIds.length === 1 && c.ExcludedUserIds.length === 1, 'lists not saved');
    let s = await simulate('Night Terror');
    expect((s.SkipReason || '').includes('turned off for user admin'), 'excluded viewer still gets pre-rolls: ' + s.SkipReason);
    s = await simulate('Night Terror', null, true);
    expect(picks(s)[0] === 'terror welcome' && s.Intros.length >= 2, '"title first" not applied: ' + picks(s));
    s = await simulate('Funny Day', null, true);
    expect((s.SkipReason || '').includes('never get pre-rolls'), 'opt-out not applied: ' + s.SkipReason);
    s = await simulate('No Intro Film', null, true);
    expect((s.SkipReason || '').includes('no-preroll'), 'opt-out tag not applied: ' + s.SkipReason);
    await page.uncheck('#liExcludedUsers input[data-user=admin]');
    await page.uncheck('#liTitleEnabled');
    await saveOk();
    s = await simulate('Night Terror');
    expect(picks(s)[0] === 'scary', 'Spooky rule should pick the horror pre-roll once per-title is off: ' + picks(s));
    s = await simulate('Kids Fun');
    const ex = (s.Excluded || []).map(e => e.Name);
    expect(ex.includes('scary') && ex.includes('Retro Cinema'), 'age-safe did not exclude R/PG before a G movie: ' + ex);
    await shot('08-playback');
  });

  // ================= CATALOG =================
  await tab('catalog');
  await step('Catalog: grid loads with thumbnails', async () => {
    await page.waitForSelector('#liCatGrid .li-tile', { timeout: 60000 });
    await page.waitForTimeout(2000);
    const ok = await page.evaluate(async () => {
      const t = document.querySelector('#liCatGrid .li-thumb');
      const url = t && getComputedStyle(t).backgroundImage.replace(/^url\("?|"?\)$/g, '');
      const img = new Image(); img.src = url;
      await new Promise(r => { img.onload = r; img.onerror = r; });
      return img.naturalWidth > 0;
    });
    expect(ok, 'thumbnail did not load');
    // Most catalog items are hosted on YouTube; their thumbnails must load too.
    const yt = await page.evaluate(async () => {
      const items = (await ApiClient.getJSON(ApiClient.getUrl('LocalPrerollsPro/Catalog', { limit: 100 }))).Items.filter(i => i.Source === 'youtube').slice(0, 3);
      const loaded = await Promise.all(items.map(i => new Promise(r => { const img = new Image(); img.onload = () => r(img.naturalWidth > 0); img.onerror = () => r(false); img.src = ApiClient.getUrl('LocalPrerollsPro/Catalog/Thumb/' + i.Id, { ApiKey: ApiClient.accessToken() }); })));
      return { n: items.length, ok: loaded.filter(Boolean).length };
    });
    expect(yt.n === 3 && yt.ok === 3, `YouTube thumbnails: ${yt.ok} of ${yt.n} loaded`);
  });
  const catTotal = async () => parseInt((await text('#liCatInfo')).split(' ')[0], 10);
  const waitCat = () => page.waitForFunction(() => document.getElementById('liCatInfo').textContent !== 'Loading…', null, { timeout: 30000 }).then(() => page.waitForTimeout(300));
  await step('Catalog: search, filters, sort and "Downloadable only" all narrow the list', async () => {
    const all = await catTotal();
    await page.fill('#liCatQ', 'acapella'); await page.waitForTimeout(700); await waitCat();
    const acapella = await catTotal();
    expect(acapella > 0 && acapella < all, `search: ${acapella} of ${all}`);
    const titles = await page.locator('#liCatGrid .li-tile strong').allInnerTexts();
    expect(titles.every(t => /acapella/i.test(t)), 'search results include non-matching titles');
    await page.fill('#liCatQ', ''); await page.waitForTimeout(700); await waitCat();
    await page.selectOption('#liCatPlatform', 'jellyfin'); await waitCat();
    expect(await catTotal() < all, 'platform filter did nothing');
    await page.selectOption('#liCatCategory', { index: 1 }); await waitCat();
    const cat = await page.locator('#liCatCategory option:checked').innerText();
    const cats = await page.locator('#liCatGrid .li-tile .li-muted').allInnerTexts();
    expect(cats.length > 0 && cats.filter(t => !t.startsWith('By')).every(t => t.startsWith(cat.replace(/ \(\d+\)$/, ''))), 'category filter mismatch');
    await page.selectOption('#liCatCategory', ''); await waitCat();
    await page.selectOption('#liCatTag', { index: 1 }); await waitCat();
    expect(await catTotal() > 0, 'tag filter returned nothing');
    await page.selectOption('#liCatTag', ''); await waitCat();
    await page.selectOption('#liCatSort', 'az'); await waitCat();
    const az = await page.locator('#liCatGrid .li-tile strong').allInnerTexts();
    expect(az.join('|') === [...az].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })).join('|'), 'A–Z sort wrong');
    await page.check('#liCatDownloadable'); await waitCat();
    expect(await page.locator('#liCatGrid a', { hasText: 'Open on prerolls.me' }).count() === 0, 'YouTube items shown with Downloadable only');
    await page.uncheck('#liCatDownloadable'); await waitCat();
    expect(await page.locator('#liCatGrid a', { hasText: 'Open on prerolls.me' }).count() > 0, 'YouTube items missing');
    expect((await page.locator('#liCatGrid').innerText()).includes("can't be downloaded here"), 'no explanation on link-only items');
  });
  await step('Catalog: Next and Previous pages', async () => {
    const first = await page.locator('#liCatGrid .li-tile strong').first().innerText();
    await page.getByRole('button', { name: 'Next ›' }).click(); await waitCat();
    expect((await text('#liCatPager')).includes('Page 2'), 'not on page 2');
    expect(await page.locator('#liCatGrid .li-tile strong').first().innerText() !== first, 'same items on page 2');
    await page.getByRole('button', { name: '‹ Previous' }).click(); await waitCat();
    expect((await text('#liCatPager')).includes('Page 1'), 'not back on page 1');
  });
  await step('Catalog: Preview plays the clip', async () => {
    await page.check('#liCatDownloadable'); await waitCat();
    await page.locator('#liCatGrid .li-tile').first().getByRole('button', { name: 'Preview' }).click();
    await page.waitForSelector('#liCatGrid .li-tile video, #liCatGrid .li-tile .li-warn', { timeout: 10000 });
  });
  await step('Catalog: Download saves the pre-roll with its tags and credit', async () => {
    const tile = page.locator('#liCatGrid .li-tile', { has: page.getByRole('button', { name: 'Download', exact: true }) }).first();
    const title = await tile.locator('strong').innerText();
    await tile.getByRole('button', { name: 'Download', exact: true }).click();
    await page.waitForFunction(t => [...document.querySelectorAll('#liCatGrid .li-tile')].some(x => x.innerText.includes(t) && x.innerText.includes('Downloaded ✓')), title, { timeout: 120000 });
    await tab('files'); await page.waitForTimeout(1500);
    const t = await text('#liFilesBody');
    expect(t.includes(title) && t.includes('prerolls.me'), 'downloaded file not listed with its tags');
    await tab('catalog'); await page.waitForTimeout(800);
  });
  await step('Catalog: subscriptions validate, refuse duplicates, pause and unfollow', async () => {
    await page.click('#liSubAdd');
    expect((await text('#liSubError')).includes('Choose a category'), 'no error for empty follow');
    await page.selectOption('#liSubValue', { index: 1 });
    await page.click('#liSubAdd');
    await page.click('#liSubAdd');
    expect((await text('#liSubError')).includes('already follow'), 'duplicate follow allowed');
    expect(await page.locator('#liSubs [data-sub]').count() === 1, 'subscription not listed');
    await page.locator('#liSubs [data-sub] input').uncheck();
    await page.getByRole('button', { name: 'Unfollow' }).click();
    expect((await text('#liSubs')).includes('Not following anything'), 'not removed');
  });
  await step('Catalog: "Save and sync now" downloads a followed category, then says up to date', async () => {
    await page.click('#liSyncNow');
    expect((await text('#liSyncStatus')).includes('Follow a category or tag first'), 'no message without subscriptions');
    const counts = await page.evaluate(async () => (await ApiClient.getJSON(ApiClient.getUrl('LocalPrerollsPro/Catalog', { platform: 'jellyfin', downloadableOnly: true, limit: 100 }))).Items.reduce((m, i) => { if (!i.OnDisk) { m[i.CategorySlug] = (m[i.CategorySlug] || 0) + 1; } return m; }, {}));
    const smallest = Object.entries(counts).sort((a, b) => a[1] - b[1])[0];
    await page.selectOption('#liSubKind', 'category');
    await page.selectOption('#liSubValue', smallest[0]);
    await page.selectOption('#liSubPlatform', 'jellyfin');
    await page.click('#liSubAdd');
    await page.click('#liSyncNow');
    await page.waitForFunction(() => !document.getElementById('liSyncNow').disabled, null, { timeout: 180000 });
    expect((await text('#liSyncStatus')).includes(`Downloaded ${smallest[1]} new pre-roll`), await text('#liSyncStatus'));
    await page.click('#liSyncNow');
    await page.waitForFunction(() => !document.getElementById('liSyncNow').disabled, null, { timeout: 60000 });
    expect((await text('#liSyncStatus')).includes('Up to date'), await text('#liSyncStatus'));
    const c = await savedConfig(page);
    expect(c.CatalogSubscriptions.length === 1 && c.CatalogSubscriptions[0].Platform === 'jellyfin', JSON.stringify(c.CatalogSubscriptions));
  });
  await step('Catalog: download folder must be a full path inside a pre-roll folder', async () => {
    await page.fill('#liCatFolder', 'relative/dir');
    await saveBlocked('download folder must be a full path');
    await page.fill('#liCatFolder', '/tmp/elsewhere');
    await saveBlocked('inside one of your pre-roll folders');
    await page.fill('#liCatFolder', prerollDir + '/downloads');
    await page.uncheck('#liCatPerCategory');
    await page.uncheck('#liAutoSync');
    await saveOk();
    const c = await savedConfig(page);
    expect(c.CatalogDownloadFolder === prerollDir + '/downloads' && !c.CatalogFolderPerCategory && !c.CatalogAutoSync, 'catalog settings not saved');
    await page.fill('#liCatFolder', ''); await page.check('#liCatPerCategory'); await page.check('#liAutoSync');
    await saveOk();
  });
  await step('Catalog: browsing never re-downloads the catalog (gentle on prerolls.me)', async () => {
    const before = (await status()).Catalog;
    for (const q of ['logo', 'christmas', 'studio', '']) { await page.fill('#liCatQ', q); await page.waitForTimeout(500); await waitCat(); }
    for (const s of ['newest', 'shortest', 'popular']) { await page.selectOption('#liCatSort', s); await waitCat(); }
    await page.evaluate(async () => { for (let i = 0; i < 5; i++) { await ApiClient.getJSON(ApiClient.getUrl('LocalPrerollsPro/Catalog', { refresh: true })); } });
    const after = (await status()).Catalog;
    expect(after.CatalogFetches === before.CatalogFetches, `catalog fetched again: ${before.CatalogFetches} -> ${after.CatalogFetches}`);
    expect(after.CatalogFetches === 1, 'catalog fetched more than once in total: ' + after.CatalogFetches);
    expect((await text('#liCatTraffic')).includes('refreshes at most once an hour'), 'traffic note missing');
    await shot('09-catalog');
  });

  // ================= TEST =================
  await tab('test');
  await step('Test: running without a title asks for one', async () => {
    await page.click('#liTestRun');
    expect((await page.locator('.li-panel[data-panel=test]').innerText()).includes('Search for a movie or episode'), 'no message');
  });
  await step('Test: no matches are explained', async () => {
    await page.fill('#liTestSearch', 'zzzz'); await page.waitForTimeout(900);
    expect((await text('#liTestItem')).includes('Nothing found for "zzzz"'), await text('#liTestItem'));
  });
  await step('Test: unsaved changes are pointed out, with a Save link', async () => {
    await tab('playback'); await page.fill('#liAvoidRepeat', '3'); await tab('test');
    expect((await text('#liTestNote')).includes('unsaved changes'), 'no note');
    await page.locator('#liTestNote').getByRole('button', { name: 'Save now' }).click();
    await page.waitForTimeout(1500); await takeAlert(page);
    expect((await text('#liTestNote').catch(() => '')) === '', 'note still shown after saving');
  });
  await step('Test: a missing date is refused; a real run explains the result', async () => {
    await page.fill('#liTestSearch', 'Kids'); await page.waitForTimeout(900);
    await page.fill('#liTestAt', '');
    await page.click('#liTestRun');
    expect((await page.locator('.li-panel[data-panel=test]').innerText()).includes('Pick a date and time'), 'no date message');
    await page.fill('#liTestAt', '2026-12-24T19:30');
    await page.selectOption('#liTestUser', '');
    await page.click('#liTestRun');
    await page.waitForSelector('#liTestResult .li-card');
    const t = await text('#liTestResult');
    expect(t.includes('No viewer, 2026-12-24 19:30') && t.includes('Left out before the rules ran') && t.includes('What the rules see') && t.includes('Chances'), t.slice(0, 300));
    await shot('10-test');
  });

  // ================= BACKUP =================
  await tab('backup');
  let exported = null;
  await step('Backup: Download settings gives a complete file', async () => {
    const [dl] = await Promise.all([page.waitForEvent('download'), page.click('#liExport')]);
    const file = path.join(OUT, 'export.json');
    await dl.saveAs(file);
    exported = JSON.parse(fs.readFileSync(file, 'utf8'));
    expect(exported.LocalPrerollsProExport === 1 && exported.Rules.length === 3 && exported.CatalogSubscriptions.length === 1, 'export incomplete');
  });
  await step('Backup: Restore explains empty, broken and wrong files', async () => {
    await page.click('#liImport');
    expect((await text('#liRestoreError')).includes('Choose a settings file or paste'), await text('#liRestoreError'));
    await page.fill('#liImportText', '{not json');
    await page.click('#liImport');
    expect((await text('#liRestoreError')).includes('not valid JSON'), await text('#liRestoreError'));
    await page.fill('#liImportText', '{"Hello": 1}');
    await page.click('#liImport');
    expect((await text('#liRestoreError')).includes('no list of rules'), await text('#liRestoreError'));
    await page.fill('#liImportText', '{"Rules": [], "IntroFolders": "oops"}');
    await page.click('#liImport');
    expect((await text('#liRestoreError')).includes('IntroFolders should be a list'), await text('#liRestoreError'));
  });
  await step('Backup: Restore from the downloaded file brings settings back', async () => {
    // Change something, then restore the export and check it is undone.
    await tab('rules');
    await page.locator('#liRulesBody tr[data-rule="Christmas"]').getByRole('button', { name: 'Delete' }).click();
    await saveOk();
    await tab('backup');
    await page.setInputFiles('#liImportFile', path.join(OUT, 'export.json'));
    await page.waitForTimeout(500);
    await page.click('#liImport');
    const a = await takeAlert(page);
    expect(a && a.includes('Backup loaded'), 'no confirmation: ' + a);
    await saveOk();
    expect((await savedConfig(page)).Rules.map(r => r.Name).includes('Christmas'), 'restore did not bring the rule back');
  });
  await step('Unsaved bar: Discard changes throws edits away', async () => {
    await tab('playback');
    await page.fill('#liAvoidRepeat', '9');
    expect(await page.locator('#liUnsaved').isVisible(), 'unsaved bar hidden');
    await page.click('#liDiscard');
    await page.waitForTimeout(2500);
    expect(await page.inputValue('#liAvoidRepeat') === '3', 'not discarded: ' + await page.inputValue('#liAvoidRepeat'));
    expect(!(await page.locator('#liUnsaved').isVisible()), 'bar still visible');
  });

  // ================= HELP =================
  await step('Help: the link at the top opens Help, and every answer expands', async () => {
    await page.locator('[data-goto=help]').click();
    expect(await page.locator('.li-panel[data-panel=help]').isVisible(), 'help not shown');
    const n = await page.locator('.li-helpdoc details').count();
    for (let i = 0; i < n; i++) { await page.locator('.li-helpdoc details summary').nth(i).click(); }
    const open = await page.locator('.li-helpdoc details[open]').count();
    expect(n >= 12 && open === n, `${open} of ${n} opened`);
    await shot('11-help');
  });

  // ================= IMPORT FROM LOCAL INTROS =================
  await step('Backup: Import Local Intros settings replaces rules and reloads', async () => {
    await tab('backup');
    await page.click('#liImportLocal');
    await page.waitForTimeout(3000);
    const a = await takeAlert(page);
    expect(a && a.includes('Imported') && a.includes('disable the Local Intros plugin'), 'no message: ' + a);
    await page.waitForTimeout(2000);
    const names = (await savedConfig(page)).Rules.map(r => r.Name).join('|');
    expect(names === 'Horror|All intros', names);
  });

  // ================= PHONE LAYOUT =================
  await step('Phone: no tab scrolls sideways at 390 px', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    const wide = [];
    for (const t of ['files', 'rules', 'preshow', 'playback', 'catalog', 'test', 'backup', 'help']) {
      await tab(t); await page.waitForTimeout(t === 'catalog' ? 1500 : 300);
      const over = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      if (over > 2) { wide.push(t + ':' + over); }
    }
    await page.screenshot({ path: path.join(OUT, '12-phone.png') });
    await page.setViewportSize({ width: 1280, height: 900 });
    expect(wide.length === 0, 'too wide: ' + wide.join(', '));
  });

  await step('No JavaScript errors anywhere', async () => { expect(log.errors.length === 0, log.errors.join(' | ')); });
  await opened.browser.close();

  const failed = results.filter(r => !r[0]);
  console.log(`\n${results.length - failed.length} of ${results.length} checks passed`);
  process.exit(failed.length);
})().catch(e => { console.error(e); process.exit(99); });
