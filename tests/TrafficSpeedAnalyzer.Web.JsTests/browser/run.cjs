// Esegue una pagina di test in Chromium (Playwright) e stampa i risultati.
const { chromium } = require('playwright');
(async () => {
  const url = process.argv[2];
  const browser = await chromium.launch({ args: ['--enable-unsafe-webgpu'] });
  const page = await browser.newPage();
  page.on('console', m => { if (m.type() === 'error') console.log('console:', m.text()); });
  page.on('pageerror', e => console.log('pageerror:', e.message));
  await page.goto(url);
  const results = await page.evaluate(() => window.testResults);
  let fail = 0;
  for (const r of results) { console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.detail ? ' — ' + r.detail : ''}`); if (!r.ok) fail++; }
  console.log(`${results.length - fail}/${results.length} superati`);
  await browser.close();
  process.exit(fail ? 1 : 0);
})();
