// UI-01: login -> tenant -> dashboard -> job wizard -> run -> artifact -> restore wizard -> restored DB verified -> cleanup, all through the real UI (owner A, host-e2e PostgreSQL job to local storage).
const L = require('./lib.cjs');

const N = String(Date.now() % 1e6), RESUME = process.env.RESUME_ARTIFACT;
const JOB = process.env.RESUME_JOB || 'gap-ui-01-' + N, DB = 'gap_ui_01_' + N;
const steps = []; const note = (k, v) => { steps.push([k, v]); console.log('  ·', k, v); };
(async () => {
  const b = await L.launch(); const ctx = await L.newCtx(b); const p = await ctx.newPage();
  const shots = [];
  let ok = true, err = '';
  try {
    if (RESUME) { const base0 = await L.login(p, 'a'); await p.goto(base0 + '/artifacts/' + RESUME, { waitUntil: 'networkidle' }); await p.waitForSelector('text=Geri yüklenebilir'); }
    else {
    await p.goto(L.BASE + '/', { waitUntil: 'networkidle' });
    note('redirect to login', p.url().endsWith('/login'));
    shots.push(await L.shot(p, 'UI-01-01-login'));
    const base = await L.login(p, 'a');
    note('landed', base.replace(L.BASE, '').replace(L.ST.a.tenantId, '<tenantA>'));
    await p.waitForSelector('text=Genel Bakış'); await p.waitForLoadState('networkidle');
    shots.push(await L.shot(p, 'UI-01-02-dashboard'));
    await p.getByRole('link', { name: 'Yeni yedekleme işi' }).first().click();
    await p.waitForURL(/backup-jobs\/new$/);
    await p.getByLabel('İş adı').fill(JOB);
    const sel = p.getByLabel('Yedekleme bilgisayarı'); await p.waitForFunction(() => [...document.querySelectorAll('select option')].some(o => o.textContent.includes('host-e2e')));
    const opts = await sel.locator('option').allInnerTexts();
    await sel.selectOption({ label: opts.find(o => o.includes('host-e2e')) });
    await p.getByRole('radio', { name: 'PostgreSQL' }).check();
    await p.getByLabel('PostgreSQL ana bilgisayarı').fill('127.0.0.1');
    await p.getByLabel('Veritabanı', { exact: true }).fill('e2e_src');
    const c = L.ST.host.pg;
    await p.getByLabel('Veritabanı kullanıcı adı').fill(c.user);
    shots.push(await L.shot(p, 'UI-01-03-wizard-source'));            // before the password is typed
    await p.getByLabel('Veritabanı parolası').fill(c.password);
    await p.getByText('Teknik ayrıntılar').first().click();
    note('defaults encrypt/trust', [await p.getByLabel('Bağlantıyı şifrele').isChecked(), await p.getByLabel('Sunucu sertifikasına güven').isChecked()].join('/'));
    await p.getByLabel('Bağlantıyı şifrele').uncheck(); await p.getByLabel('Sunucu sertifikasına güven').check();
    await p.getByRole('button', { name: 'İleri' }).click(); await p.waitForSelector('text=Adım 2 / 5');
    shots.push(await L.shot(p, 'UI-01-04-wizard-storage'));
    await p.getByRole('button', { name: 'İleri' }).click(); await p.waitForSelector('text=Adım 3 / 5');
    await p.getByRole('button', { name: 'Elle çalıştır' }).click();
    shots.push(await L.shot(p, 'UI-01-05-wizard-schedule'));
    await p.getByRole('button', { name: 'İleri' }).click(); await p.waitForSelector('text=Adım 4 / 5');
    await p.getByRole('button', { name: 'İleri' }).click(); await p.waitForSelector('text=Adım 5 / 5');
    shots.push(await L.shot(p, 'UI-01-06-wizard-summary'));
    await p.getByRole('button', { name: 'Kaydet' }).click(); await p.waitForURL(/\/backup-jobs$/);
    await p.getByLabel('İş, yedekleme bilgisayarı veya veritabanı ara').fill(JOB); await p.waitForTimeout(600);
    shots.push(await L.shot(p, 'UI-01-07-jobs-list'));
    await p.getByRole('button', { name: 'Şimdi çalıştır' }).first().click();
    await p.getByRole('link', { name: 'Denemeyi aç' }).click(); await p.waitForURL(/backup-runs\/[0-9a-f-]{36}$/);
    const runId = p.url().split('/').pop(); note('runId', runId);
    shots.push(await L.shot(p, 'UI-01-08-run-started'));
    await p.waitForSelector('text=Başarılı', { timeout: 900000 }); await p.waitForTimeout(500);
    shots.push(await L.shot(p, 'UI-01-09-run-succeeded'));
    await p.getByRole('tab', { name: /Denemeler/ }).click();
    await p.getByRole('link', { name: 'Yedek dosyasını aç' }).click(); await p.waitForURL(/artifacts\/[0-9a-f-]{36}$/);
    const artifactId = p.url().split('/').pop(); note('artifactId', artifactId);
    await p.waitForSelector('text=Geri yüklenebilir'); shots.push(await L.shot(p, 'UI-01-10-artifact'));
    }
    await p.getByRole('link', { name: 'Veritabanını geri yükle' }).click(); await p.waitForURL(/restore$/);
    await p.waitForSelector('text=Hangi yedekleme bilgisayarı geri yüklesin?');
    note('restore step1 agent preselected', await p.locator('select').first().inputValue() !== '');
    shots.push(await L.shot(p, 'UI-01-11-restore-source'));
    await p.getByRole('button', { name: /Devam et/ }).click(); await p.waitForSelector('text=Hangi PostgreSQL sunucusuna geri yüklensin?');
    const conn = p.getByLabel('Bağlantı', { exact: true }); const copts = await conn.locator('option').allInnerTexts();
    await conn.selectOption({ label: copts.find(o => o.startsWith(JOB)) });
    await p.getByLabel('Yeni Veritabanı Adı').fill(DB);
    shots.push(await L.shot(p, 'UI-01-12-restore-target'));
    await p.getByRole('button', { name: /Ön Kontrolü Çalıştır/ }).click();
    await p.waitForSelector('text=/Ön kontrol|Kontrol/', { timeout: 60000 });
    await p.waitForTimeout(8000);
    const rows = await p.locator('main table tbody tr').allInnerTexts(); note('precheck rows', JSON.stringify(rows.map(r => r.replace(/\s+/g, ' '))));
    await p.getByRole('button', { name: /Devam et/ }).click(); await p.waitForTimeout(800);
    shots.push(await L.shot(p, 'UI-01-14-restore-confirm'));
    await p.getByRole('button', { name: 'Geri yüklemeyi başlat' }).click();
    await p.waitForURL(/\/restores\/[0-9a-f-]{36}$/, { timeout: 30000 }); note('restoreId', p.url().split('/').pop());
    await p.waitForSelector('text=Başarılı', { timeout: 600000 }); await p.waitForTimeout(800);
    shots.push(await L.shot(p, 'UI-01-15-restore-succeeded'));
    const exists = L.hostPg(`select count(*) from pg_database where datname='${DB}'`);
    const want = L.hostPg(L.PGQ, 'e2e_src'), got = exists === '1' ? L.hostPg(L.PGQ, DB) : 'missing';
    note('restored db digest', `${got} vs source ${want}`); if (got !== want) throw new Error('digest mismatch ' + got + ' vs ' + want);
    L.hostPg(`drop database "${DB}" with (force)`); note('dropped', L.hostPg(`select count(*) from pg_database where datname='${DB}'`) === '0');
  } catch (e) { ok = false; err = String(e).slice(0, 600); console.log('ERR', err); await L.shot(p, 'UI-01-error').catch(() => {}); }
  console.log(JSON.stringify({ ok, shots, JOB, DB, steps }));
  L.rec('UI-01', 'ui01 end-to-end ' + (RESUME ? '(restore leg only)' : 'full'), ok, { shots, steps, error: err });
  await b.close();
})();
