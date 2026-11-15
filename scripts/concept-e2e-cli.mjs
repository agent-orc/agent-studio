#!/usr/bin/env node
// The remote coding host runs this fake CLI inside its scratch Git worktree.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = join(import.meta.dirname, '.');
if (existsSync(join(root, 'concept-implementation-ready'))) {
  const code = join(process.cwd(), 'src');
  mkdirSync(code, { recursive: true });
  writeFileSync(join(code, 'gate-probe.mjs'), 'export const delivered = "remote concept implementation";\n');
  mkdirSync(process.env.JOB_RESULTS_DIR, { recursive: true });
  writeFileSync(join(process.env.JOB_RESULTS_DIR, 'status.md'), '# Status\n\nDelivered src/gate-probe.mjs.\n');
  writeFileSync(join(process.env.JOB_RESULTS_DIR, 'deliverables.md'), '# Deliverables\n\n- src/gate-probe.mjs\n');
  writeFileSync(join(root, 'concept-implementation-run'), 'done\n');
  process.stdout.write(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: 'Delivered the implementation probe. [[TASK_DONE]]' } }) + '\n');
  process.stdout.write(JSON.stringify({ type: 'turn.completed', usage: { input_tokens: 1, output_tokens: 1 } }) + '\n');
  process.exit(0);
}
const taskKey = readFileSync(join(root, 'concept-task-key'), 'utf8').trim();
const documentKey = `${taskKey.replace(/-\d+$/, '')}-W1`;
const roundFile = join(root, 'concept-round');
let round = 0;
try { round = Number(readFileSync(roundFile, 'utf8')); } catch { /* first run */ }
round += 1;
writeFileSync(roundFile, `${round}\n`);
const folder = join(process.cwd(), 'docs', 'remote-concept');
mkdirSync(folder, { recursive: true });
const descriptor = {
  schemaVersion: 1, id: 'remote-concept', key: documentKey, title: 'Disposable remote concept',
  summary: 'A remote Dossier with three owner choices and two bounded slices.',
  entrypoint: 'index.html', status: 'decision-pending', phase: 'shaping',
  updatedAt: new Date().toISOString(), sourceTaskKeys: [taskKey],
  implementationTasks: [
    { id: 'delivery', title: 'Implement the delivery API', promptMarkdown: 'Add one bounded delivery API slice with a focused test.', decisionId: 'storage' },
    { id: 'review', title: 'Implement the review view', promptMarkdown: 'Add one bounded review view slice with a focused browser check.', decisionId: 'review' },
  ],
};
writeFileSync(join(folder, 'workbench.json'), JSON.stringify(descriptor, null, 2) + '\n');
const decision = (id, question, a, b) => `<div data-decision-id="${id}" data-decision-kind="single" data-decision-label="${question}"><h3>${question}</h3><label data-option-id="${id}-a" data-option-label="${a} (recommended)"><input type="radio" name="${id}" checked>${a} (recommended)</label><label data-option-id="${id}-b" data-option-label="${b}"><input type="radio" name="${id}">${b}</label></div>`;
writeFileSync(join(folder, 'index.html'), `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Disposable remote concept</title><style>body{font:16px system-ui;max-width:70ch;margin:auto;padding:2rem;line-height:1.5}section{margin-block:2rem}</style></head><body><main><h1>Disposable remote concept</h1><p>Brief round ${round}. The owner reviews three choices before promotion.</p><section data-concept-section="alternatives"><h2>Alternatives</h2><p>Use a local document store or a remote source.</p></section><section data-concept-section="recommendation"><h2>Recommendation</h2><p>Use the local document store for this disposable flow.</p></section><section data-concept-section="evidence"><h2>Evidence</h2><p>The remote CLI authored this Dossier during round ${round}.</p></section><section data-concept-section="open-decisions"><h2>Open decisions</h2>${decision('storage','Where should the document live?','Local store','Remote source')}${decision('review','How should review appear?','Inline review','Separate view')}${decision('gate','When should verification run?','Before promotion','After promotion')}</section></main></body></html>\n`);
mkdirSync(process.env.JOB_RESULTS_DIR, { recursive: true });
writeFileSync(join(process.env.JOB_RESULTS_DIR, 'status.md'), `# Status\n\nDelivered the Dossier at docs/remote-concept/index.html in round ${round}.\n`);
writeFileSync(join(process.env.JOB_RESULTS_DIR, 'deliverables.md'), '# Deliverables\n\n- docs/remote-concept/index.html\n- docs/remote-concept/workbench.json\n');
process.stdout.write(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: `Delivered Dossier round ${round}. [[TASK_DONE]]` } }) + '\n');
process.stdout.write(JSON.stringify({ type: 'turn.completed', usage: { input_tokens: 1, output_tokens: 1 } }) + '\n');
