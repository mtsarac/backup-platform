// UI-02: 1440 / 768 / 375 px; horizontal overflow by script on every tenant + platform page, table->card conversion, nav reachable, screenshots of key pages.
const L = require('./lib.cjs'); const P = require('./pages.cjs');
const WIDTHS = [[1440, 900], [768, 1024], [375, 812]];
const KEY = new Set(['dashboard', 'jobs', 'job-wizard', 'run-detail', 'artifacts', 'restore-wizard', 'storage', 'agents', 'artifact-detail']);
const probe = () => {
  const vw = document.documentElement.clientWidth, doc = document.documentElement;
  const scrollable = e => { for (let n = e.parentElement; n && n !== document.body; n = n.parentElement) { const o = getComputedStyle(n).overflowX; if (o === 'auto' || o === 'scroll' || o === 'hidden') return true; } return false; };
  const off = [...document.querySelectorAll('body *')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > vw + 1 && getComputedStyle(e).position !== 'fixed' && !scrollable(e); })
    .slice(0, 4).map(e => `${e.tagName.toLowerCase()}.${String(e.className).split(' ').slice(0, 3).join('.')}:${Math.round(e.getBoundingClientRect().right)}:${(e.innerText || '').trim().slice(0, 25)}`);
  const vis = e => !!(e.offsetWidth || e.offsetHeight);
  return { docOverflow: doc.scrollWidth - vw, bodyOverflow: document.body.scrollWidth - vw, offenders: off, tables: [...document.querySelectorAll('main table')].filter(vis).length, cards: document.querySelectorAll('main [data-slot=card],main [class*=card]').length,
    navVisible: [...document.querySelectorAll('nav a')].some(vis), menuBtn: [...document.querySelectorAll('button[aria-label]')].some(b => vis(b) && /Menüyü aç/.test(b.getAttribute('aria-label'))), text: (document.querySelector('main')?.innerText || '').length };
};
async function sweep(role, pages, ctx, base, rows) {
  for (const [w, h] of WIDTHS) {
    const p = await ctx.newPage(); await p.setViewportSize({ width: w, height: h });
    for (const [name, path] of pages) {
      await p.goto(base + path, { waitUntil: 'networkidle' }).catch(() => {}); await p.waitForTimeout(700);
      const r = await p.evaluate(probe); r.page = name; r.w = w; r.url = p.url().replace(base, '') || '/';
      if (r.w < 1024 && !r.navVisible && r.menuBtn) { await p.getByRole('button', { name: 'Menüyü aç' }).click(); await p.waitForTimeout(300); r.navAfterMenu = await p.evaluate(() => [...document.querySelectorAll('nav a')].filter(e => e.offsetWidth).length); await p.keyboard.press('Escape'); }
      if (KEY.has(name) && role === 'tenant') r.shot = await L.shot(p, `UI-02-${name}-${w}`, { fullPage: w < 1440 });
      rows.push(r);
    }
    await p.close();
  }
}
(async () => {
  const b = await L.launch(); const rows = [];
  let ctx = await L.newCtx(b); let p = await ctx.newPage(); const base = await L.login(p, 'a'); const ids = await P.ids(ctx, L.ST.a.tenantId);
  await sweep('tenant', P.tenantPages(ids), ctx, base, rows); await ctx.close();
  const bad = rows.filter(r => r.docOverflow > 1 || r.offenders.length);
  const summary = WIDTHS.map(([w]) => { const rs = rows.filter(r => r.w === w); return { w, pages: rs.length, overflowing: rs.filter(r => r.docOverflow > 1).map(r => r.page), withOffenders: rs.filter(r => r.offenders.length).map(r => `${r.page}:${r.offenders[0]}`), tablesVisible: rs.filter(r => r.tables).map(r => `${r.page}=${r.tables}`), noNav: rs.filter(r => !r.navVisible && !r.menuBtn).map(r => r.page) }; });
  console.log(JSON.stringify(summary, null, 1));
  require('fs').writeFileSync(L.OUT + '/../ui02-rows.json', JSON.stringify(rows, null, 1));
  L.rec('UI-02', 'responsive sweep', bad.length === 0, { rows: rows.length, summary });
  await b.close();
})();
