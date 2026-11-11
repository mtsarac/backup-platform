// Page inventory shared by the UI checks: real ids are discovered through the logged-in session so every detail page is exercised.
const L = require('./lib.cjs');
async function ids(ctx, tenantId) {
  const T = `/api/tenants/${tenantId}`;
  const first = async (path, pick = x => x) => { const d = await L.get(ctx, T + path); const items = Array.isArray(d) ? d : d.items || []; return items.length ? pick(items[0]) : null; };
  const runs = await L.get(ctx, T + '/backup-runs?pageSize=50'); const ok = (runs.items || []).find(r => r.status === 'Succeeded');
  const art = await first('/backup-artifacts?pageSize=20', a => a.id); const rst = await first('/restores?pageSize=20', r => r.id || r.restoreId);
  return {
    agent: ((await L.get(ctx, T + '/agents')) || []).find(a => a.hostName === 'host-e2e')?.agentId, job: await first('/backup-jobs?pageSize=5', j => j.id), run: ok && ok.id, artifact: art, restore: rst,
    conn: await first('/storage-connections', c => c.id || c.connectionId),
  };
}
const tenantPages = (i) => [
  ['dashboard', ''], ['agents', '/agents'], i.agent && ['agent-detail', '/agents/' + i.agent], ['enrollment', '/enrollment'], ['jobs', '/backup-jobs'], ['job-wizard', '/backup-jobs/new'],
  i.job && ['job-edit', `/backup-jobs/${i.job}/edit`], ['runs', '/backup-runs'], i.run && ['run-detail', '/backup-runs/' + i.run], ['artifacts', '/artifacts'],
  i.artifact && ['artifact-detail', '/artifacts/' + i.artifact], i.artifact && ['restore-wizard', `/artifacts/${i.artifact}/restore`], ['restores', '/restores'],
  i.restore && ['restore-detail', '/restores/' + i.restore], ['storage', '/storage'], ['storage-usage', '/storage/usage'], ['storage-providers', '/storage/providers'],
  ['storage-conn-new', '/storage/connections/new'], i.conn && ['storage-conn-detail', '/storage/connections/' + i.conn], ['discovery', '/discovery'], ['bandwidth', '/upload-bandwidth'],
  ['notifications', '/notifications'], ['encryption', '/encryption'],
].filter(Boolean);
module.exports = { ids, tenantPages };
