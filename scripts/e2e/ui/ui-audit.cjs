const L = require('./lib.cjs'); const P = require('./pages.cjs'); const fs=require('fs');
(async()=>{const b=await L.launch(),ctx=await L.newCtx(b),p=await ctx.newPage(),base=await L.login(p,'a');
const ids=await P.ids(ctx,L.ST.a.tenantId), result={};
for(const [name,path] of [['dashboard',''],['jobs','/backup-jobs'],['wizard','/backup-jobs/new'],['artifacts','/artifacts'],['agent','/agents/'+ids.agent]]){
 await p.goto(base+path,{waitUntil:'networkidle'}); await p.addScriptTag({path:L.G+'axe-core/axe.min.js'}); const a=await p.evaluate(async()=>await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa']}}));
 result[name]=a.violations.map(v=>({id:v.id,impact:v.impact,count:v.nodes.length,example:v.nodes[0]?.html.slice(0,250)})); await L.shot(p,`UI-07-${name}-axe`);
}
await p.goto(base,{waitUntil:'networkidle'}); await p.keyboard.press('Tab'); result.focus=await p.evaluate(()=>({text:document.activeElement?.innerText,tag:document.activeElement?.tagName}));
await p.getByRole('button',{name:'Görünüm'}).click(); result.dialog=await L.dump(p,'body');
await p.getByRole('button',{name:'Temayı değiştir'}).click(); result.dark=await p.evaluate(()=>({class:document.documentElement.className,bg:getComputedStyle(document.body).backgroundColor})); await L.shot(p,'UI-08-dark');
await p.getByRole('button',{name:'Yüksek kontrast'}).click(); result.contrast=await p.evaluate(()=>({class:document.documentElement.className,bg:getComputedStyle(document.body).backgroundColor})); await L.shot(p,'UI-08-high-contrast');
await p.emulateMedia({forcedColors:'active'}); await L.shot(p,'UI-07-forced-colors');
await p.emulateMedia({forcedColors:'none'}); await p.getByRole('button',{name:'Yüksek kontrast'}).click(); await p.getByRole('button',{name:'Temayı değiştir'}).click(); await p.keyboard.press('Escape'); await L.shot(p,'UI-08-light');
result.targets=await p.evaluate(()=>[...document.querySelectorAll('main button, main a')].filter(e=>e.getClientRects().length).map(e=>({name:(e.innerText||e.getAttribute('aria-label')||'').trim().slice(0,45),w:Math.round(e.getBoundingClientRect().width),h:Math.round(e.getBoundingClientRect().height)})).filter(e=>e.w<44||e.h<44).slice(0,20));
await p.getByRole('button',{name:'Dili değiştir'}).click(); await p.waitForTimeout(500); result.en=(await p.locator('body').innerText()).slice(0,2500); await L.shot(p,'UI-03-english');
await p.getByRole('button',{name:'Change language'}).click(); await p.waitForTimeout(500); result.tr=(await p.locator('body').innerText()).slice(0,1200); await L.shot(p,'UI-03-turkish');
fs.writeFileSync(L.OUT+'/../ui-audit.json',JSON.stringify(result,null,2)); console.log(JSON.stringify(result).slice(0,7000)); await b.close();})().catch(e=>{console.error(String(e));process.exitCode=1});
