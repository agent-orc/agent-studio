// One-off operator migration for the four cards named by the Decision cards Dossier.
// Dry run is the default. Run --apply only after reviewing the printed inventory.
// Uses the Task API; never writes workspace files. AGT-2795 is deliberately excluded.
import http from 'node:http';
import { createHash } from 'node:crypto';

const ids = ['AGT-2792', 'AGT-2736', 'AGT-2737', 'AGT-2738'];
const host = process.env.TASKBOARD_HOST ?? '127.0.0.1';
const port = Number(process.env.TASKBOARD_PORT ?? 5031);
const apply = process.argv.includes('--apply');

function request(method, path, body) {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : JSON.stringify(body);
    const req = http.request({ hostname: host, port, path, method, headers: {
      'X-Client-Id': 'local-default', 'Content-Type': 'application/json',
      ...(payload === undefined ? {} : { 'Content-Length': Buffer.byteLength(payload) }),
    } }, res => {
      let text = '';
      res.on('data', chunk => { text += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, text }));
    });
    req.on('error', reject);
    if (payload !== undefined) req.write(payload);
    req.end();
  });
}

function section(text, start, end) {
  const from = text.search(start);
  if (from < 0) return '';
  const tail = text.slice(from);
  const stop = tail.search(end);
  return (stop < 0 ? tail : tail.slice(0, stop)).trim();
}

export function plan(card) {
  const info = card.info;
  const prompt = card.promptMarkdown ?? '';
  const label = `${info.key ?? info.id} ${info.state} ${info.kind}`;
  if (info.key === 'AGT-2795') return { label, reason: 'concept card excluded' };
  if (info.kind === 'decision') return { label, reason: 'already migrated' };
  if (info.kind !== 'task') return { label, reason: 'not an ordinary task' };
  if (!['1-preparation', '5e-escalated'].includes(info.state))
    return { label, reason: 'settled or outside an active decision lane; preserve its history' };
  if (/operator decision recorded|operator decisions recorded|decision 1 .*option [a-d]|the operator adopts the recorded recommendation/i.test(prompt))
    return { label, reason: 'operator choice is already recorded in prose' };

  if (info.key !== 'AGT-2792')
    return { label, reason: 'no self-contained option set in this card; do not infer from another document' };
  const choice = section(prompt, /^## Decision needed \(operator\)/m, /^## Requirements/m);
  const match = choice.match(/Option A, lock file: ([\s\S]*?)\n\nOption B, no lock file: ([\s\S]*)/);
  if (!match) return { label, reason: 'recognisable two-option request not found' };
  const common = section(prompt, /^## Requirements \(after the decision\)/m, /^## Out of scope/m);
  if (!common) return { label, reason: 'implementation requirements not found' };
  const requirements = (option, description) => [{
    title: `Implement AGT-2792 option ${option}: Stable release identity`,
    promptMarkdown: `Implement the operator's chosen option ${option} for AGT-2792.\n\nOption consequences and work:\n${description.trim()}\n\n${common}\n\nSource decision: AGT-2792.`,
  }];
  const decision = {
    question: 'How should the Stable release contract identify restored backend dependencies?',
    options: [
      { id: 'A', label: 'Lock file', consequences: match[1].trim(),
        effort: 'Every dependency change updates the lock file.', risk: 'A missing or stale lock blocks release.',
        requirements: requirements('A', match[1]) },
      { id: 'B', label: 'No lock file', consequences: match[2].trim(),
        effort: 'Change generator, contract validation, update service and documentation.',
        risk: 'Identity must include package integrity metadata.',
        requirements: requirements('B', match[2]) },
    ],
    recommendedOptionId: 'A',
    recommendationReason: 'The recorded feasibility probe found a valid lock and a successful locked-mode restore; this preserves the existing manifest contract.',
    decider: 'operator', dependants: [], appliesTo: [],
  };
  return { label, decision, digest: createHash('sha256').update(prompt).digest('hex'),
    reason: 'two options and implementation requirements recognised' };
}

async function main() {
  console.log(`Decision migration ${apply ? 'APPLY' : 'DRY RUN'} on ${host}:${port}`);
  let planned = 0; let changed = 0; let skipped = 0; let failed = 0;
  for (const id of ids) {
    const response = await request('GET', `/api/tasks/${id}`);
    if (response.status !== 200) { console.log(`${id}: GET ${response.status}; skip`); skipped++; continue; }
    const card = JSON.parse(response.text);
    const item = plan(card);
    console.log(`${item.label}: ${item.decision ? 'MIGRATE' : 'SKIP'}: ${item.reason}`);
    if (!item.decision) { skipped++; continue; }
    planned++;
    for (const option of item.decision.options)
      console.log(`  ${option.id}: ${option.label}; ${option.consequences}`);
    console.log(`  recommendation: ${item.decision.recommendedOptionId}; ${item.decision.recommendationReason}`);
    if (!apply) continue;
    const path = `/api/tasks/${encodeURIComponent(card.info.id)}/decision/migrate?watchPath=${encodeURIComponent(card.info.watchPath)}`;
    const result = await request('POST', path, { expectedPromptSha256: item.digest, decision: item.decision });
    if (result.status === 200) { changed++; console.log(`  changed: ${result.text}`); }
    else { failed++; console.error(`  FAILED ${result.status}: ${result.text}`); }
  }
  console.log(`Summary: planned=${planned} changed=${changed} skipped=${skipped} failed=${failed}`);
  if (!apply) console.log('Review the inventory, then rerun with --apply against a backend containing the migration endpoint.');
  if (failed) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href)
  main().catch(error => { console.error(error); process.exitCode = 1; });
