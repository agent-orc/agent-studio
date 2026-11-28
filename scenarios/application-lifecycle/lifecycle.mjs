// The AGT-3178 first run, including both sides of a failed production health check.
import assert from 'node:assert/strict';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { loadChromium } from '../../scripts/release/product-scenario/browser.mjs';
import { totp } from '../../scripts/release/product-scenario/session.mjs';
import { installation, nextTotpWindow, projectName, studio } from './installation.mjs';
import { waitForCompletion } from './completion.mjs';

const dir = path.join(process.env.JOB_RESULTS_DIR, 'lifecycle');
await mkdir(dir, { recursive: true });
const site = await installation();
const browser = await (await loadChromium()).launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, ignoreHTTPSErrors: true });
page.setDefaultTimeout(60000);
const rows = [];
const wish = 'Build a Click Counter website with Count and Reset buttons and a test.';
const env = () => site.owner.environments(projectName);
const quota = () => site.owner.workspaceQuota(site.group.id);
const visit = relative => page.goto(new URL(relative, studio).href, { waitUntil: 'domcontentloaded' });
const createCard = async (id, title, promptMarkdown) => {
  await site.session.post('/api/tasks/', { id, title, promptMarkdown, project: site.project.id, targetState: '2-ready' });
  return site.owner.task(id, site.project.id);
};
let card, baseline;
async function step(id, title, run) {
  const row = { id, title, startedAtUtc: new Date().toISOString(), evidence: [], status: 'passed' };
  try { await run(text => row.evidence.push(text)); }
  catch (error) {
    row.status = 'failed';
    row.reason = error.message;
    process.exitCode = 1;
  }
  row.screenshot = id + '.png';
  await page.screenshot({ path: path.join(dir, row.screenshot), mask: [page.locator('input[type=password],input[name=secondFactorCode]')] });
  row.endedAtUtc = new Date().toISOString();
  rows.push(row);
  console.log(id, row.status, row.reason ?? '');
  const pending = path.join(dir, 'steps.json.tmp');
  await writeFile(pending, JSON.stringify(rows, null, 2));
  await rename(pending, path.join(dir, 'steps.json'));
}
async function stage(revision, key, note) {
  const release = await site.publish(revision, key);
  const artifact = release.artifactDigests[0];
  note('Studio created release ' + release.version + ' for ' + release.commitSha + ', artifact ' + artifact);
  const result = await site.deploy('stage', artifact);
  assert.equal(result.healthStatus, 200);
  const view = await env();
  assert.equal(view.environments.find(e => e.declaration.kind === 'stage').deployedArtifact, artifact);
  assert.ok(view.promotions.some(p => p.artifact === artifact && p.status === 'pending'));
  note('Stage job ' + result.job + ' returned HTTP 200 and Studio requested production approval.');
  return artifact;
}
async function promote(artifact, expected, note) {
  await site.approve(artifact);
  const result = await site.deploy('production', artifact);
  note('Owner approved. Production job ' + result.job + ' probed /health.txt and received HTTP ' + result.healthStatus);
  assert.equal(result.healthStatus, expected);
}
try {
  await step('01-sign-in', '1. Sign in as the simulated Owner', async note => {
    await nextTotpWindow(site.loginTime);
    await visit('/');
    const gate = page.getByTestId('auth-gate');
    await gate.locator('[name=username]').fill(site.credentials.login);
    await gate.locator('[name=password]').fill(site.credentials.password);
    await gate.locator('[name=secondFactorCode]').fill(totp(site.credentials.totpSecret, Date.now()));
    await gate.getByRole('button', { name: 'Sign in', exact: true }).click();
    await gate.waitFor({ state: 'hidden' });
    await page.getByTestId('studio-shell').waitFor({ timeout: 8000 }).catch(() => {});
    note('Studio accepted the password and second factor. The sign-in form closed. API role ' + site.user.role);
  });
  await step('02-create', '2. Register a new static-web project and set its weekly quota', async note => {
    const project = await site.initProject();
    const q = await quota();
    note('Registered local template checkout as ' + project.id + ' in ' + site.group.id + '. Weekly limits ' + JSON.stringify(q.limits));
    assert.equal(q.limits.hostingSlots, 2);
    await visit('/api/projects');
  });
  await step('02-environments', 'Declare stage and production', async note => {
    const c = await createCard('declare-environments', 'Declare stage and production', 'Declare the two local static deployment environments.');
    note('Studio created ' + c.key + ' in ' + c.state + '. A fixture commit supplies the declarations.');
    const view = await site.declare();
    note(view.environments.map(e => e.declaration.kind + ' ' + e.declaration.address).join(', '));
    assert.equal(view.environments.length, 2);
    await site.startCoding();
    await visit('/api/projects/Click%20Counter/environments');
  });
  await step('03-chat', '3. Send the wish in project chat and check for new cards', async note => {
    const before = new Set((await site.owner.tasks(site.project.id)).map(task => task.id));
    const reply = await site.session.send('POST', '/api/runner/Click%20Counter/orchestrator-chat', { body: { text: wish } });
    note('Chat HTTP ' + reply.status + '. ' + (reply.body?.reply?.text ?? reply.body?.error ?? reply.body?.message ?? '').slice(0, 600));
    await visit('/api/runner/Click%20Counter/orchestrator-chat');
    const chatPath = '/api/runner/Click%20Counter/orchestrator-chat';
    const deadline = Date.now() + 120000;
    let confirmed = false, answer;
    do {
      card = (await site.owner.tasks(site.project.id)).find(task => !before.has(task.id));
      if (card) break;
      const thread = await site.session.get(chatPath);
      const lastReply = (thread.turns ?? []).findLast(turn => turn.role !== 'user');
      answer = lastReply?.text || lastReply?.errorDetail || lastReply?.errorMessage;
      if (!confirmed && (answer || Date.now() > deadline - 60000)) {
        confirmed = true;
        const confirm = await site.session.send('POST', chatPath, { body: { text: 'Yes, please create the cards for this now.' } });
        note('Confirmation HTTP ' + confirm.status);
      } else if (confirmed && answer && Date.now() > deadline - 30000) break;
      await sleep(4000);
    } while (Date.now() < deadline);
    note('Wish: ' + wish + ' Last chat answer: ' + (answer?.slice(0, 600) ?? 'not observed'));
    await page.reload({ waitUntil: 'domcontentloaded' });
    if (!card) {
      card = await createCard('counter-feature', 'Build Click Counter', wish);
      note('The local fallback submitted the wish through the task API. Studio assigned ' + card.key + ' in ' + card.state);
      throw Error('No card from the chat provider after confirmation. The fixture task lets release verification continue.');
    }
  });
  await step('04-coding', '4. Inspect coding, review and quota consumption', async note => {
    try {
      await waitForCompletion(() => site.owner.task(card.id, site.project.id),
        lane => note(card.key + ' observed in ' + lane + '.'));
    } finally {
      note('Runner readiness: ' + (await site.readiness()).map(c => c.label + ': ' + c.detail).join(' '));
      const q = await quota();
      note('Quota usage ' + JSON.stringify(q.usage) + '. Meter complete ' + q.meterComplete);
      await visit('/api/workspaces/' + site.group.id + '/quota');
      await visit('/');
      await page.getByTestId('studio-titlebar-settings').press('Enter');
      await page.getByTestId('workspace-settings-rail-workspaces').click();
      await page.getByTestId('settings-workspace-quota-' + site.group.id).click();
      await page.getByTestId('workspace-quota').scrollIntoViewIfNeeded();
    }
  });
  await step('05-stage', '5. Stage the tested local release and exercise the counter', async note => {
    baseline = await stage('feature', card.key, note);
    await visit(site.targets.stage.address);
    await page.getByRole('button', { name: 'Count' }).click({ clickCount: 2 });
    assert.equal(await page.locator('#count').innerText(), '2');
    await page.getByRole('button', { name: 'Reset' }).click();
    assert.equal(await page.locator('#count').innerText(), '0');
    note('Chromium counted 0 to 2 and reset to 0.');
  });
  await step('06-production', '6. Approve the staged digest for production', async note => {
    assert.ok(baseline, 'A staged artifact is required');
    await promote(baseline, 200, note);
    const prod = (await env()).environments.find(e => e.declaration.kind === 'production');
    assert.equal(prod.deployedArtifact, baseline);
    assert.equal(prod.health, 'healthy');
    const response = await visit(site.targets.production.address);
    assert.equal(response.status(), 200);
    note('Production reports healthy for the stage digest. Browser HTTP ' + response.status());
  });
  await step('07a-rollback', '7a. Remove the health file, approve, and inspect rollback and the failure card', async note => {
    const fault = await createCard('counter-fault', 'Remove the production health file', 'Seed the production health failure.');
    const bad = await stage('fault', fault.key, note);
    await promote(bad, 404, note);
    await visit(site.targets.production.healthUrl);
    const rollback = await site.deploy('production', baseline);
    assert.equal(rollback.healthStatus, 200);
    const prod = (await env()).environments.find(e => e.declaration.kind === 'production');
    assert.equal(prod.deployedArtifact, baseline);
    const failure = (await site.owner.tasks(site.project.id)).find(c => c.tags?.includes('production-deployment'));
    note('Restored digest ' + prod.deployedArtifact + '. Latest history ' + prod.history[0]?.status + '. Failure card ' + (failure?.key ?? 'not observed'));
    assert.equal(prod.history[0]?.status, 'rolled-back');
    assert.ok(failure, 'No failure card from Studio');
  });
  await step('07b-repair', '7b. Restore the health file with a test and redeploy', async note => {
    const fix = await createCard('counter-repair', 'Restore the production health file', 'Restore health.txt and test it before publishing.');
    const repaired = await stage('repair', fix.key, note);
    await promote(repaired, 200, note);
    const response = await visit(site.targets.production.healthUrl);
    assert.equal(response.status(), 200);
    assert.equal((await page.locator('body').innerText()).trim(), 'repair');
    assert.equal((await env()).environments.find(e => e.declaration.kind === 'production').deployedArtifact, repaired);
    note('The browser read the repaired health file and Studio recorded the repaired digest.');
  });
  await step('08-quota', '8. Lower the weekly limit to measured use and check admission', async note => {
    const before = await quota();
    await site.session.put('/api/workspaces/' + site.group.id + '/quota', { ...before.limits, modelTokensPerWeek: before.usage.modelTokens });
    const q = await quota();
    const admission = await site.admission();
    note('Limit ' + q.limits.modelTokensPerWeek + '. Status ' + q.status + '. ' + JSON.stringify(admission));
    await visit('/api/workspaces/' + site.group.id + '/quota');
    assert.equal(q.codingAllowed, false);
    assert.equal(admission.allowed, false);
    await visit('/');
    const projectRow = page.getByTestId('studio-explorer-project-' + projectName);
    await projectRow.dblclick();
    const banner = page.getByTestId('workspace-quota-status');
    await banner.waitFor();
    note('Studio header: ' + await banner.innerText());
    assert.equal(await banner.getAttribute('data-status'), 'blocked');
  });
  await step('09-stop', 'Teardown. Stop both deployments', async note => {
    for (const e of (await env()).environments.filter(item => item.deployedArtifact)) {
      const kind = e.declaration.id;
      await site.session.post('/api/projects/Click%20Counter/environments/' + kind + '/stop');
      const result = await site.deploy(kind, e.deployedArtifact);
      note(kind + ' stop job ' + result.job + '. Website HTTP ' + result.healthStatus);
    }
    const stopped = (await env()).environments;
    await visit('/api/projects/Click%20Counter/environments');
    assert.ok(stopped.every(item => !item.deployedArtifact || item.health === 'stopped'));
  });
  await step('10-remove-project', 'Teardown. Remove the fixture project', async note => {
    const id = site.project.id;
    await site.session.request('DELETE', '/api/projects/' + id, { body: {
      confirmation: id, reason: 'Remove the disposable Click Counter scenario project after verification.' } });
    await visit('/api/projects');
    assert.ok(!(await site.owner.projects()).some(p => p.id === id));
    note(id + ' was removed from the registry. The fixture launcher disposes of its local Git origin on exit.');
  });
  await step('11-remove-workspace', 'Teardown. Remove the fixture workspace', async note => {
    await site.session.delete('/api/workspaces/' + site.group.id);
    await visit('/api/workspaces');
    assert.ok(!(await site.owner.workspaces()).some(w => w.id === site.group.id));
    note(site.group.id + ' was removed from the workspace list.');
  });
} finally {
  await browser.close();
  await site.close();
}
