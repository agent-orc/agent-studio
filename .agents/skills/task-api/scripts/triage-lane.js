// Template: bulk-classify + move tasks from one lane.
//
// Pattern from the 2026-05-11 human-review triage of 108 tasks. Reads each
// task's status.md and aspect-*.md, classifies into action buckets, then
// mutates through the task API. Reading files is fine for classification;
// writing/moving must go through /api/tasks.

import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031);
const TARGET_PROJECT = process.env.TASKBOARD_PROJECT ?? 'Agent Task Processor';
const SOURCE_STATE = process.env.TASKBOARD_SOURCE_STATE ?? '5-human-review';

const BOGUS_ASPECT = /Aspect runner produced no parseable verdict/i;

function request(method, reqPath, bodyObj = null) {
  return new Promise(resolve => {
    const body = bodyObj ? JSON.stringify(bodyObj) : '';
    const req = http.request({
      hostname: HOST,
      port: PORT,
      path: reqPath,
      method,
      headers: {
        'Content-Type': 'application/json',
        'X-Client-Id': 'local-default',
        'Content-Length': Buffer.byteLength(body),
      },
    }, res => {
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => resolve({ status: res.statusCode, body: data }));
    });
    req.on('error', e => resolve({ status: -1, body: e.message }));
    if (body) req.write(body);
    req.end();
  });
}

async function resolveWatchPath() {
  const res = await request('GET', '/api/watch-paths');
  if (res.status !== 200) throw new Error(`watch-path lookup failed: ${res.status} ${res.body}`);
  const entries = JSON.parse(res.body);
  const entry = entries.find(p => p.name === TARGET_PROJECT)
    ?? entries.find(p => p.name?.toLowerCase().includes(TARGET_PROJECT.toLowerCase()));
  if (!entry?.path) throw new Error(`No watchPath found for project "${TARGET_PROJECT}"`);
  return entry.path;
}

function classify(lane) {
  const out = { DONE: [], OPEN: [], CHECK: [] };
  const folders = fs.readdirSync(lane).filter(d => fs.statSync(path.join(lane, d)).isDirectory());
  for (const slug of folders) {
    const folder = path.join(lane, slug);
    let realConcerns = 0;
    let bogusConcerns = 0;
    let hasOpen = false;
    let hasDone = false;
    let openItemsText = '';
    const statusPath = path.join(folder, 'status.md');
    if (fs.existsSync(statusPath)) {
      const text = fs.readFileSync(statusPath, 'utf-8');
      const m = text.match(/##\s*open\s+items\s*\n([\s\S]*?)(?=\n##|\n$|$)/i);
      if (m) {
        const bullets = m[1].split('\n')
          .filter(l => l.trim().startsWith('-') && l.trim().length > 5)
          .filter(l => !/^-\s*(none|none\.|\(none\))$/i.test(l.trim()));
        if (bullets.length) {
          hasOpen = true;
          openItemsText = bullets.slice(0, 8).join('\n');
        }
      }
      if (/result:\s*(complete|done|success)/i.test(text)) hasDone = true;
    }
    for (const f of fs.readdirSync(folder).filter(f => f.startsWith('aspect-') && f.endsWith('.md'))) {
      const t = fs.readFileSync(path.join(folder, f), 'utf-8');
      const sm = t.match(/^status:\s*(\w+)/mi);
      const summary = (t.match(/^summary:\s*(.+)$/mi) || [, ''])[1];
      if (sm && sm[1].toLowerCase() === 'concerns') {
        if (BOGUS_ASPECT.test(summary)) bogusConcerns++;
        else realConcerns++;
      }
    }
    if (hasOpen) out.OPEN.push({ slug, folder, openItemsText });
    else if (hasDone || bogusConcerns > 0) out.DONE.push({ slug });
    else out.CHECK.push({ slug });
  }
  return out;
}

async function moveTo(watchPath, slug, targetState) {
  const reqPath = `/api/tasks/${encodeURIComponent(slug)}/move?watchPath=${encodeURIComponent(watchPath)}`;
  const res = await request('POST', reqPath, { targetState, runIntent: 'revoke' });
  return { slug, status: res.status, body: res.body.slice(0, 200) };
}

async function appendReissueNote(watchPath, item) {
  const promptPath = path.join(item.folder, 'prompt.md');
  if (!fs.existsSync(promptPath)) return false;
  const original = fs.readFileSync(promptPath, 'utf-8');
  if (original.includes('Human Review Reissue Note')) return false;
  const today = new Date().toISOString().slice(0, 10);
  const note = [
    '',
    '---',
    '',
    `## Human Review Reissue Note (${today})`,
    '',
    'Triaged in `5-human-review`. The previous run documented these open items:',
    '',
    item.openItemsText || '(no specific open items found, please re-evaluate)',
    '',
    'Please address these points or document why they are out of scope.',
    '',
  ].join('\n');
  const reqPath = `/api/tasks/${encodeURIComponent(item.slug)}/files/${encodeURIComponent('prompt.md')}`
    + `?watchPath=${encodeURIComponent(watchPath)}`;
  const res = await request('PUT', reqPath, { content: original + note });
  if (res.status < 200 || res.status >= 300) {
    console.error(`prompt update failed for ${item.slug}: ${res.status} ${res.body.slice(0, 200)}`);
    return false;
  }
  return true;
}

async function main() {
  const watchPath = await resolveWatchPath();
  const lane = path.join(watchPath, SOURCE_STATE);
  const buckets = classify(lane);
  console.log('Buckets:');
  for (const k of Object.keys(buckets)) console.log(`  ${k}: ${buckets[k].length}`);

  const arg = process.argv[2];
  if (arg === '--move-done') {
    let ok = 0;
    let fail = 0;
    for (const { slug } of buckets.DONE) {
      const r = await moveTo(watchPath, slug, '6-completed');
      r.status === 200 ? ok++ : fail++;
    }
    console.log(`DONE -> 6-completed: ok=${ok} fail=${fail}`);
  } else if (arg === '--reissue-open') {
    let ok = 0;
    let fail = 0;
    for (const item of buckets.OPEN) {
      await appendReissueNote(watchPath, item);
      const r = await moveTo(watchPath, item.slug, '2-ready');
      r.status === 200 ? ok++ : fail++;
    }
    console.log(`OPEN -> 2-ready (with note): ok=${ok} fail=${fail}`);
  } else {
    console.log('Usage: --move-done | --reissue-open');
    console.log('Run without flags to just print bucket counts (dry-run).');
    console.log('\nCHECK bucket (manual triage):');
    for (const { slug } of buckets.CHECK) console.log('  ' + slug);
  }
}

main().catch(e => { console.error('failed:', e); process.exit(1); });
