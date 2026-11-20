// Opens one harness page at 2x and waits for it to draw. shots.mjs drives it.
// PLAYWRIGHT_CORE points at playwright-core's index.mjs, CHROMIUM at a Chromium binary, PORT at the server.
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
export const PORT = process.env.PORT || '8874';
const CHROMIUM = process.env.CHROMIUM || `${homedir()}/Library/Caches/ms-playwright/chromium-1148/chrome-mac/Chromium.app/Contents/MacOS/Chromium`;
let chromium;
try {
  ({ chromium } = await import(process.env.PLAYWRIGHT_CORE || 'playwright-core'));
} catch (e) {
  console.error(`Cannot load playwright-core (${e.message}). Set PLAYWRIGHT_CORE to the path of playwright-core's index.mjs.`);
  process.exit(1);
}
if (!existsSync(CHROMIUM)) {
  console.error(`No Chromium at ${CHROMIUM}. Set CHROMIUM to a Chromium binary.`);
  process.exit(1);
}

// Opens a tab at the given window size, 800x900 unless named, with the dashboard header hidden, since the
// docs crop it out.
export async function open(page, vp) {
  const b = await chromium.launch({ executablePath: CHROMIUM });
  const p = await b.newPage({ viewport: vp || { width: 800, height: 900 }, deviceScaleFactor: 2 });
  await p.goto(`http://127.0.0.1:${PORT}/index.html?page=${page}`);
  await p.waitForFunction(() => window.__ready);
  await p.waitForTimeout(1500);
  await p.addStyleTag({ content: '#jfHeader { display: none; } #host { padding-top: 0; }' });
  await p.waitForTimeout(200);
  return { b, p };
}
