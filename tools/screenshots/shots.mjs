// Takes every docs/screenshots image from the harness at 2x, dark theme, generic data.
// Usage: node shots.mjs [Name,Name...] to take all or only the named ones. Each PNG goes to docs/screenshots/.
import { fileURLToPath } from 'node:url';
import { open } from './shoot.mjs';
const OUT = fileURLToPath(new URL('../../docs/screenshots/', import.meta.url));
const only = process.argv[2];
const PAD = 8;

// Saves the box of one element, with a little room around it.
async function element(p, handle, file, pad = PAD) {
  const r = await p.evaluate((el) => { const b = el.getBoundingClientRect(); return { x: b.left, y: b.top + scrollY, w: b.width, h: b.height }; }, handle);
  const width = await p.evaluate(() => document.documentElement.clientWidth);
  const x = Math.max(0, r.x - pad);
  await p.screenshot({ path: OUT + file, fullPage: true, clip: { x, y: Math.max(0, r.y - pad), width: Math.min(width - x, r.w + 2 * pad), height: r.h + 2 * pad } });
}
const $ = (p, selector) => p.evaluateHandle((s) => document.querySelector(s), selector);
// The page's content from its title down.
async function content(p, file) { await element(p, await $(p, '.content-primary'), file); }
// The page's content from its title down to the bottom of one element.
async function contentTo(p, selector, file) {
  const r = await p.evaluate((s) => {
    const top = document.querySelector('.content-primary').getBoundingClientRect();
    const end = document.querySelector(s).getBoundingClientRect();
    return { x: top.left, y: top.top + scrollY, w: top.width, h: end.bottom - top.top };
  }, selector);
  await p.screenshot({ path: OUT + file, fullPage: true, clip: { x: Math.max(0, r.x - PAD), y: Math.max(0, r.y - PAD), width: r.w + 2 * PAD, height: r.h + 3 * PAD } });
}
// The box from the top of one element to the bottom of another.
async function span(p, from, to, file) {
  const r = await p.evaluate(([a, b]) => {
    const top = document.querySelector(a).getBoundingClientRect();
    const end = document.querySelector(b).getBoundingClientRect();
    const box = document.querySelector(a).parentElement.getBoundingClientRect();
    return { x: box.left, y: top.top + scrollY, w: box.width, h: end.bottom - top.top };
  }, [from, to]);
  await p.screenshot({ path: OUT + file, fullPage: true, clip: { x: Math.max(0, r.x - PAD), y: Math.max(0, r.y - PAD), width: r.w + 2 * PAD, height: r.h + 2 * PAD } });
}
// Opens the collapsible section whose header targets an id, or whose title reads a text, and returns the section.
async function section(p, target) {
  const h = await p.evaluateHandle((t) => {
    const header = document.querySelector(`.base-collapsible-header[data-target="${t}"]`)
      || [...document.querySelectorAll('.base-collapsible-header')].find((x) => x.querySelector('.base-collapsible-title')?.textContent.trim() === t && x.offsetParent);
    if (header.classList.contains('collapsed')) header.click();
    return header.closest('.base-collapsible-section');
  }, target);
  await p.waitForTimeout(600);
  return h;
}
async function syncType(p, value) {
  await p.selectOption('#syncTypeDropdown', value);
  await p.waitForTimeout(2500);
}
async function metadataRow(p, name) {
  await syncType(p, 'metadata');
  await p.evaluate((n) => [...document.querySelectorAll('#metadataSyncItemsTableContainer .serversync-item-name')].find((e) => e.textContent === n).click(), name);
  await p.waitForTimeout(1200);
  return p.evaluateHandle(() => document.querySelector('#metadataSyncItemDetailModal .base-dialog-content'));
}

const shots = {
  'settings/Servers': { page: 'servers', act: (p) => contentTo(p, '#btnAddServer', 'settings/Servers.png') },
  'settings/Connection': { page: 'servers', act: async (p) => element(p, await section(p, 'serverStepConnection'), 'settings/Connection.png') },
  'settings/Authentication': { page: 'servers', act: async (p) => element(p, await section(p, 'serverStepAuthentication'), 'settings/Authentication.png') },
  'settings/Check Link': { page: 'servers', act: async (p) => {
      await section(p, 'serverStepAuthentication');
      await p.click('#btnCheckPeer'); await p.waitForTimeout(1200);
      await element(p, await $(p, '#peerCheckArea'), 'settings/Check Link.png');
    } },
  'settings/Linking': { page: 'servers', act: async (p) => element(p, await section(p, 'serverStepStream'), 'settings/Linking.png') },
  'settings/Library Mapping': { page: 'servers', act: async (p) => element(p, await section(p, 'serverStepLibraries'), 'settings/Library Mapping.png') },
  'settings/User Mapping': { page: 'servers', act: async (p) => element(p, await section(p, 'serverStepUsers'), 'settings/User Mapping.png') },
  'settings/Storage': { page: 'storage', vp: { width: 800, height: 4200 }, act: async (p) => { await section(p, 'Libraries'); await section(p, 'Permanent copies'); await p.waitForTimeout(800); await content(p, 'settings/Storage.png'); } },
  'Statistics': { page: 'statistics', act: (p) => content(p, 'Statistics.png') },
  'settings/Settings': { page: 'settings', act: (p) => content(p, 'settings/Settings.png') },
  'settings/Metadata': { page: 'settings', act: async (p) => element(p, await section(p, 'Metadata'), 'settings/Metadata.png') },
  'settings/People': { page: 'settings', act: async (p) => element(p, await section(p, 'People'), 'settings/People.png') },
  'settings/Several Servers': { page: 'settings', act: async (p) => {
      await section(p, 'Users');
      await span(p, '#userSyncOptions > .base-subsection-title', '#userSyncOptions > .serversync-settings-group', 'settings/Several Servers.png');
    } },
  'Queue': { page: 'sync', act: async (p) => { await syncType(p, 'queue'); await content(p, 'Queue.png'); } },
  'Queue Phone': { page: 'sync', vp: { width: 375, height: 812 }, act: async (p) => { await syncType(p, 'queue'); await content(p, 'Queue Phone.png'); } },
  'Sync Table': { page: 'sync', vp: { width: 800, height: 1400 }, act: async (p) => { await syncType(p, 'metadata'); await content(p, 'Sync Table.png'); } },
  'Item Detail': { page: 'sync', vp: { width: 800, height: 1100 }, act: async (p) => element(p, await metadataRow(p, 'Harbor Lights'), 'Item Detail.png', 0) },
  'Metadata Item': { page: 'sync', vp: { width: 800, height: 1100 }, act: async (p) => {
      const dialog = await metadataRow(p, 'Harbor Lights');
      // Scrolls the dialog so its Media segments rows sit at the top.
      await p.evaluate((d) => {
        const row = [...d.querySelectorAll('.serversync-modal-section-header td')].find((td) => td.textContent.trim().toLowerCase() === 'media segments');
        let box = row.parentElement;
        while (box && box !== d.parentElement && !(box.scrollHeight > box.clientHeight + 4 && getComputedStyle(box).overflowY !== 'visible')) box = box.parentElement;
        box.scrollTop += row.getBoundingClientRect().top - box.getBoundingClientRect().top - 60;
      }, dialog);
      await p.waitForTimeout(400);
      await element(p, dialog, 'Metadata Item.png', 0);
    } }
};

for (const [name, s] of Object.entries(shots)) {
  if (only && !only.split(',').includes(name)) continue;
  const { b, p } = await open(s.page, s.vp);
  await s.act(p);
  const errs = await p.evaluate(() => window.__errs.concat((window.__unrouted || []).map((r) => 'no stub for ' + r)));
  console.log(name, errs.length ? JSON.stringify(errs) : 'ok');
  await b.close();
}
