import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { UsageError, parseConfig, personLogin, readSignIn } from '../config.mjs';
import { playScenario } from '../play.mjs';
import { buildReport, renderHtml, writeReport } from '../report.mjs';
import { quotaDelta } from '../runner.mjs';
import { cardTitle, multipartFields, widen } from '../steps.mjs';
import { fakeBrowserFactory, fakeClock, startFakeStudio } from './fake-studio.mjs';

async function scratch(t) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'product-scenario-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  return directory;
}

async function signInFile(directory, fields) {
  const file = path.join(directory, 'sign-in.json');
  await writeFile(file, JSON.stringify(fields));
  await chmod(file, 0o600);
  return file;
}

function argv(studio, file, reportDir, extra = []) {
  return ['--studio-url', studio.url, '--user-file', file, '--report-dir', reportDir, '--poll-seconds', '5', ...extra];
}

async function play(t, studioOptions = {}, { extra = [], clock = fakeClock(), studio: given } = {}) {
  const studio = given ?? await startFakeStudio({ clock, ...studioOptions });
  if (!given) t.after(() => studio.close());
  const directory = await scratch(t);
  const file = await signInFile(directory, { login: studio.login, password: studio.password });
  const reportDir = path.join(directory, 'report');
  const config = parseConfig(argv(studio, file, reportDir, extra), {});
  const browserFactory = fakeBrowserFactory(studio);
  const report = await playScenario(config, { clock, browserFactory });
  return { report, studio, reportDir, config, browserFactory };
}

const step = (report, id) => report.steps.find(row => row.id === id);
const failures = report => JSON.stringify(report.steps.filter(row => row.status !== 'passed' && row.phase !== 'teardown'), null, 2);

test('a full playthrough passes every step and writes the report with evidence', async t => {
  const { report, studio, reportDir } = await play(t);

  assert.equal(report.status, 'passed', failures(report));
  assert.deepEqual(report.steps.map(row => row.id), [
    'sign-in-owner', 'project', 'leftovers', 'invite', 'wish', 'delivery', 'feedback', 'accept', 'release', 'refusals',
    'retire-card', 'disable-people', 'remove-project-url', 'delete-people']);
  assert.deepEqual(report.people.map(person => [person.id, person.role, person.productionApproval]),
    [['pm', 'Product', false], ['designer', 'Product', false], ['manager', 'Product', true]]);

  // Step 2: two questions, a quota estimate, and the card only after the confirmation.
  const wish = step(report, 'wish').evidence;
  assert.equal(wish.questions.length, 2);
  assert.ok(wish.questions.every(text => text.endsWith('?')), wish.questions.join(' | '));
  assert.match(wish.proposal, /20,000 to 60,000 tokens/);
  assert.match(wish.card, /^CC-\d+: A reset confirmation and a history of the last ten values$/);
  assert.match(wish.productChat.reply, /example/);
  const card = [...studio.tasks.values()][0];
  assert.equal(card.createdBy, report.people[0].login, 'the product manager created the card');

  // Step 3: preview and before and after screenshots.
  const delivery = step(report, 'delivery').evidence;
  assert.equal(delivery.preview.status, 'ready');
  assert.match(delivery.preview.address, /preview\.internal$/);
  assert.ok(delivery.screenshots.some(caption => /^Before/.test(caption)) && delivery.screenshots.some(caption => /^After/.test(caption)));

  // Step 4: the feedback reached the fix round and the preview updated.
  assert.equal(report.feedback.comment, 'secondary style, smaller');
  assert.equal(report.feedback.selector, '#reset');
  assert.equal(report.feedback.previewAddress, studio.stage);
  assert.match(report.feedback.crop, /^attachments\/[0-9a-f]+\.png$/);
  assert.match(report.feedback.fixRoundPrompt, /^Preview feedback for this card's next fix round:/);
  assert.equal(report.feedback.resetButton.smaller, true);
  const feedback = step(report, 'feedback').evidence;
  assert.notEqual(feedback.previewBefore, feedback.previewAfter);
  assert.equal(feedback.sent.comment, 'secondary style, smaller');
  assert.equal(feedback.sent.bodyCaptured, true);

  // Step 5: the product manager accepted; only the manager could release; production runs the stage digest.
  assert.equal(card.acceptedBy, report.people[0].login);
  const release = step(report, 'release').evidence;
  assert.equal(release.productManagerApproval.status, 403);
  assert.equal(release.managerApproval.status, 200);
  assert.equal(release.productionArtifact, release.artifact);
  assert.equal(release.sameDigestAsStage, true);
  assert.equal(studio.project.environments.production.history[0].approvedBy, report.people[2].login);
  assert.deepEqual(release.productionResetButton.pixels, { width: 80, height: 28 });

  // Step 6: every Product person is refused credentials and host settings.
  assert.equal(report.refusals.filter(row => row.request.startsWith('GET')).length, 12);
  assert.ok(report.refusals.every(row => row.refused && row.expected), JSON.stringify(report.refusals));
  assert.ok(report.refusals.some(row => /remote-hosts/.test(row.request)));
  assert.ok(report.refusals.some(row => /credential-inventory/.test(row.request)));
  assert.ok(report.refusals.some(row => /promotions\/approve/.test(row.request) && row.actor === 'Product manager'));

  // Quota use per step and in total.
  assert.ok(step(report, 'delivery').usage.modelTokens > 0);
  assert.ok(step(report, 'feedback').usage.modelTokens > 0);
  assert.ok(report.totals.modelTokens >= step(report, 'delivery').usage.modelTokens + step(report, 'feedback').usage.modelTokens);

  // Teardown disables the people, removes their bindings and the added Project URL; deletion is a visible gap.
  assert.equal(report.teardownStatus, 'skipped');
  assert.equal(step(report, 'delete-people').status, 'skipped');
  assert.match(step(report, 'delete-people').reason, /no route that deletes a user/);
  for (const person of report.people) assert.equal(studio.users.find(user => user.username === person.login).disabled, true);
  assert.deepEqual(studio.bindings, []);
  assert.deepEqual(studio.project.urls, []);
  assert.deepEqual(Object.values(step(report, 'retire-card').evidence.previews), ['removed']);
  assert.deepEqual(step(report, 'retire-card').evidence.archived, []);

  // Times, screenshots of chat and preview per step, and the HTML report.
  for (const row of report.steps) {
    assert.ok(!Number.isNaN(Date.parse(row.startedAtUtc)) && !Number.isNaN(Date.parse(row.endedAtUtc)), row.id);
    assert.equal(typeof row.durationSeconds, 'number');
  }
  for (const id of ['invite', 'wish', 'delivery', 'feedback', 'accept', 'release', 'refusals'])
    assert.ok(step(report, id).screenshots.length > 0, id);
  const files = await readdir(path.join(reportDir, 'screenshots'));
  for (const shot of report.steps.flatMap(row => row.screenshots)) assert.ok(files.includes(path.basename(shot.file)), shot.file);
  const html = await readFile(path.join(reportDir, 'product.html'), 'utf8');
  assert.match(html, /Result: <strong>passed<\/strong>/);
  assert.match(html, /Feedback that reached the fix round/);
  assert.match(html, /secondary style, smaller/);
  assert.match(html, /Refused actions/);
});

test('the report and the requests never carry a password or a one-time link', async t => {
  const { studio, reportDir } = await play(t);
  const invited = studio.users.filter(user => user.role === 'contributor');
  assert.equal(invited.length, 3);
  for (const name of ['product.json', 'product.html']) {
    const text = await readFile(path.join(reportDir, name), 'utf8');
    assert.ok(!text.includes(studio.password), name);
    for (const user of invited) assert.ok(!text.includes(user.password), `${name}: ${user.username}`);
    assert.ok(!/inv\.[0-9a-f]+\.[0-9a-f]+/.test(text), `${name} holds an invitation token`);
  }
});

test('every write carries the session CSRF token and a same-origin header', async t => {
  const { studio } = await play(t);
  const writes = studio.requests.filter(request => request.method !== 'GET' && !/\/auth\/(login|invite\/accept)$/.test(request.path));
  assert.ok(writes.length > 10);
  for (const request of writes) {
    assert.ok(request.headers['x-csrf-token'], `${request.method} ${request.path}`);
    assert.equal(request.headers['sec-fetch-site'], 'same-origin');
  }
});

test('a Product role that cannot send preview feedback fails step 4 with the refusal and still tears down', async t => {
  const { report, studio } = await play(t, { feedbackDenied: true });
  assert.equal(report.status, 'failed');
  const feedback = step(report, 'feedback');
  assert.equal(feedback.status, 'failed');
  assert.match(feedback.reason, /refused the designer's feedback \(403 role-denied\)/);
  assert.ok(report.refusals.some(row => row.action === 'Send feedback on the preview' && !row.expected));
  assert.equal(step(report, 'accept').status, 'skipped');
  assert.equal(step(report, 'release').status, 'skipped');
  assert.equal(step(report, 'refusals').status, 'passed');
  assert.equal(report.feedback, null);
  assert.deepEqual(step(report, 'retire-card').evidence.archived, [[...studio.tasks.values()][0].key]);
  assert.ok(studio.users.filter(user => user.role === 'contributor').every(user => user.disabled));
});

test('a preview that never becomes ready fails step 3 with the preview job evidence', async t => {
  const { report } = await play(t, { previewFails: true });
  const delivery = step(report, 'delivery');
  assert.equal(delivery.status, 'failed');
  assert.match(delivery.reason, /card preview did not become ready \(last status failed\)/);
  assert.match(JSON.stringify(delivery.evidence.previewJobs), /Preview run zone is not configured/);
  assert.equal(step(report, 'feedback').status, 'skipped');
});

test('a fix round that ignores the feedback fails step 4 with both button sizes', async t => {
  const { report } = await play(t, { fixIgnoresFeedback: true });
  const feedback = step(report, 'feedback');
  assert.equal(feedback.status, 'failed');
  assert.match(feedback.reason, /reset button is not smaller/);
  assert.deepEqual(feedback.evidence.before, { width: 120, height: 40 });
  assert.equal(report.feedback.resetButton.smaller, false);
});

test('a reachable credential route fails step 6 and names it', async t => {
  const { report } = await play(t, { reachable: ['/api/secrets'] });
  const refusals = step(report, 'refusals');
  assert.equal(refusals.status, 'failed');
  assert.match(refusals.reason, /reached GET \/api\/secrets/);
  assert.equal(report.refusals.filter(row => !row.refused).length, 3);
});

test('a project that is not live stops setup with the lifecycle prerequisite', async t => {
  const { report } = await play(t, { projectLive: false });
  assert.equal(step(report, 'project').status, 'failed');
  assert.match(step(report, 'project').reason, /Production does not run healthy/);
  assert.equal(step(report, 'invite').status, 'skipped');
});

test('a sign-in that is not the Owner stops at setup with the reason', async t => {
  const { report } = await play(t, { ownerRole: 'operator' });
  assert.equal(step(report, 'sign-in-owner').status, 'failed');
  assert.match(step(report, 'sign-in-owner').reason, /as the Owner/);
  assert.equal(step(report, 'wish').status, 'skipped');
});

test('a rerun after a run without teardown disables the leftovers, archives the open card and passes again', async t => {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock, feedbackDenied: true });
  t.after(() => studio.close());
  const first = await play(t, {}, { clock, studio, extra: ['--teardown', 'never'] });
  assert.equal(first.report.teardownStatus, 'not-run');
  assert.equal(studio.users.filter(user => user.role === 'contributor' && !user.disabled).length, 3);
  const open = [...studio.tasks.values()][0];
  assert.equal(open.state, '5-human-review');

  clock.sleep(60_000);
  const second = await play(t, {}, { clock, studio });
  const leftovers = step(second.report, 'leftovers').evidence;
  assert.equal(leftovers.disabledUsers.length, 3);
  assert.deepEqual(leftovers.archivedCards, [open.key]);
  assert.equal(open.state, '7-archive');
  assert.equal(studio.users.filter(user => user.role === 'contributor').length, 6, 'each run invites its own logins');
  assert.equal(studio.users.filter(user => user.role === 'contributor' && !user.disabled).length, 0);
  assert.equal(step(second.report, 'invite').status, 'passed');
});

test('teardown only disables what an earlier run left, archives its open card and removes its Project URL', async t => {
  const clock = fakeClock();
  const studio = await startFakeStudio({ clock, feedbackDenied: true });
  t.after(() => studio.close());
  await play(t, {}, { clock, studio, extra: ['--teardown', 'never'] });
  assert.equal(studio.project.urls.length, 1);
  const cleanup = await play(t, {}, { clock, studio, extra: ['--teardown', 'only'] });
  assert.deepEqual(cleanup.report.steps.map(row => row.id), ['retire-card', 'disable-people', 'remove-project-url', 'delete-people']);
  assert.equal(step(cleanup.report, 'disable-people').evidence.disabled.length, 3);
  assert.ok(studio.users.filter(user => user.role === 'contributor').every(user => user.disabled));
  const open = [...studio.tasks.values()][0];
  assert.deepEqual(step(cleanup.report, 'retire-card').evidence.archived, [open.key]);
  assert.equal(open.state, '7-archive');
  assert.equal(open.preview.status, 'removed');
  assert.deepEqual(step(cleanup.report, 'remove-project-url').evidence.removed, ['url-1']);
  assert.deepEqual(studio.project.urls, []);
});

test('configuration refuses passwords and tokens outside the sign-in file', async t => {
  const required = ['--studio-url', 'https://s.test', '--user-file', 'f'];
  assert.throws(() => parseConfig([...required, '--password', 'x'], {}), UsageError);
  const tokenShaped = ['api', 'a'.repeat(32), 'b'.repeat(43)].join('.');
  assert.throws(() => parseConfig(required, { SOME_VALUE: tokenShaped }), /token-shaped/);
  assert.throws(() => parseConfig(required.slice(2), {}), /--studio-url is required/);
  assert.throws(() => parseConfig([...required, '--teardown', 'sometimes'], {}), /--teardown must be one of/);
  assert.throws(() => parseConfig([...required, '--login-prefix', 'Bad Prefix'], {}), /--login-prefix/);
  assert.throws(() => parseConfig([...required, '--poll-seconds', '0'], {}), /positive number/);
  const config = parseConfig(required, { JOB_RESULTS_DIR: '/tmp/results' });
  assert.equal(config.reportDir, '/tmp/results');
  assert.equal(config.project, 'Click Counter');
  assert.equal(personLogin(config, { id: 'designer' }, '20261109t080000z'), 'product-scenario-designer-20261109t080000z');
  assert.ok(personLogin(config, { id: 'designer' }, '20261109t080000z').length <= 64);

  const directory = await scratch(t);
  const open = path.join(directory, 'open.json');
  await writeFile(open, JSON.stringify({ login: 'a', password: 'b' }));
  await chmod(open, 0o644);
  if (process.platform !== 'win32') assert.throws(() => readSignIn(open), /accessible to group or others/);
  const good = await signInFile(directory, { login: 'a', password: 'b' });
  assert.deepEqual(readSignIn(good), { login: 'a', password: 'b' });
});

test('helpers: the card title, the marked area and the multipart fields', async () => {
  assert.equal(cardTitle(`  ${'x'.repeat(120)} `), 'x'.repeat(100));
  assert.deepEqual(widen({ x: 0.005, y: 0.5, width: 0.1, height: 0.05 }), { x: 0, y: 0.49, width: 0.12, height: 0.07 });
  assert.deepEqual(widen({ x: 0.95, y: 0.95, width: 0.05, height: 0.05 }), { x: 0.94, y: 0.94, width: 0.06, height: 0.06 });
  const form = new FormData();
  form.set('crop', new Blob([Buffer.from([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }), 'crop.png');
  form.set('comment', 'secondary style, smaller');
  form.set('previewAddress', 'http://v1.test:8081');
  form.set('selector', '#reset');
  const body = Buffer.from(await new Response(form).arrayBuffer()).toString('latin1');
  assert.deepEqual(multipartFields(body), { comment: 'secondary style, smaller', previewAddress: 'http://v1.test:8081', selector: '#reset' });
  assert.deepEqual(quotaDelta({ usage: { modelTokens: 10, computeCoreHours: 0.5, hostingSlots: 2 } },
    { usage: { modelTokens: 25, computeCoreHours: 0.75, hostingSlots: 3 } }), { modelTokens: 15, computeCoreHours: 0.25, hostingSlots: 1 });
});

test('the HTML report escapes evidence and lists refusals and feedback', async t => {
  const row = (id, phase, status, evidence = {}) => ({ id, title: id, phase, actor: 'Designer', status, startedAtUtc: '2026-11-09T08:00:00.000Z',
    endedAtUtc: '2026-11-09T08:00:01.000Z', durationSeconds: 1, evidence, screenshots: [], usage: null });
  const report = buildReport({
    scenario: { id: 'product', description: 'Product' },
    run: { runId: 'r', studioOrigin: 'https://s.test', platform: 'v1', project: 'P',
      startedAtUtc: '2026-11-09T08:00:00.000Z', endedAtUtc: '2026-11-09T08:00:02.000Z', durationSeconds: 2 },
    steps: [row('a', 'scenario', 'passed', { reply: '<script>x</script>' }), row('t', 'teardown', 'skipped')],
    feedback: { comment: '<b>smaller</b>' },
    refusals: [{ actor: 'Designer', action: 'Open hosts', request: 'GET /api/v1/management/remote-hosts', status: 403, code: 'role-denied', refused: true, expected: true }],
  });
  assert.equal(report.status, 'passed');
  assert.equal(report.teardownStatus, 'skipped');
  const html = renderHtml(report);
  assert.ok(html.includes('&lt;script&gt;x&lt;/script&gt;') && !html.includes('<script>x'));
  assert.ok(html.includes('&lt;b&gt;smaller&lt;/b&gt;'));
  assert.match(html, /role-denied/);

  const directory = await scratch(t);
  const secret = 'correct-horse-battery';
  await writeReport(directory, { ...report, steps: [{ ...report.steps[0], reason: `refused ${secret}` }] }, [secret]);
  const written = await readFile(path.join(directory, 'product.json'), 'utf8');
  assert.ok(!written.includes(secret) && written.includes('refused [redacted]'));
});
