// Find tasks across all projects + lanes.
//
// The server does not have a search/filter endpoint today; instead it
// returns the full set and the client filters. The full payload is small
// (~1-2 MB for hundreds of tasks) so client-side filter is fast enough.
//
// Usage:
//   node find-tasks.js                          # list every task, grouped by lane
//   node find-tasks.js --lane 2-ready           # one lane only
//   node find-tasks.js --grep "codex"           # case-insensitive id/title match
//   node find-tasks.js --project "Lotta"        # project name contains
//   node find-tasks.js --lane 4-auto-review --grep "session"
//
// Output is one line per match: <lane>  <project>  <slug>  -  <title>

import { taskApiRequest } from './access.js';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031);

function arg(name) {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : null;
}
const wantLane = arg('--lane');
const wantGrep = arg('--grep');
const wantProject = arg('--project');

const req = taskApiRequest({
  hostname: HOST, port: PORT, path: '/api/tasks/grouped', method: 'GET',
  headers: { 'X-Client-Id': 'local-default' },
}, res => {
  let body = '';
  res.on('data', c => body += c);
  res.on('end', () => {
    let groups;
    try { groups = JSON.parse(body); }
    catch (e) { console.error('parse failed:', e.message); process.exit(1); }
    // Response shape: { backlog: [], preparation: [], ready: [], progress: [], ... }
    const laneAlias = {
      backlog: '0-backlog', preparation: '1-preparation', orchestratorPrep: '1a-orchestrator-prep',
      needsHumanReview: '5-human-review', ready: '2-ready', progress: '3-progress',
      failedPickup: '3a-failed-pickup', autoReview: '4-auto-review', humanReview: '5-human-review',
      completed: '6-completed', archive: '7-archive',
    };
    let total = 0;
    for (const [key, jobs] of Object.entries(groups)) {
      if (!Array.isArray(jobs)) continue;
      const laneName = laneAlias[key] || key;
      if (wantLane && laneName !== wantLane && key !== wantLane) continue;
      for (const j of jobs) {
        if (wantProject && !(j.projectName || '').toLowerCase().includes(wantProject.toLowerCase())) continue;
        const hay = `${j.id || ''} ${j.title || ''}`.toLowerCase();
        if (wantGrep && !hay.includes(wantGrep.toLowerCase())) continue;
        console.log(`${laneName}\t${j.projectName || '?'}\t${j.id}  -  ${j.title || ''}`);
        total++;
      }
    }
    console.error(`\n${total} match(es).`);
  });
});
req.on('error', e => { console.error('ERR', e.message); process.exit(1); });
req.end();
