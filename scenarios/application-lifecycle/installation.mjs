// Local equivalents of GitHub provisioning, the release gate and the deploy host.
// Studio owns all users, cards, release records, job leases and promotion decisions.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { SessionClient, totp } from '../../scripts/release/product-scenario/session.mjs';
import { deployNextJob } from './deploy-job.mjs';
import { serveGit, startWorker } from './worker.mjs';
import { ProductApi } from '../../scripts/release/product-scenario/studio-api.mjs';

export const projectName = 'Click Counter';
export const workspace = process.env.PRODUCT_FIXTURE_WORKSPACE;
export const studio = process.argv[2];
const repo = path.join(workspace, 'counter');
const projectApi = '/api/projects/' + encodeURIComponent(projectName);
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const git = (...args) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' }).trim();
const json = (file, value) => writeFileSync(file, JSON.stringify(value));

export async function installation() {
  const credentials = JSON.parse(readFileSync(path.join(workspace, 'owner.json')));
  const session = new SessionClient({ url: studio });
  const owner = new ProductApi(session);
  const loginTime = Date.now();
  const user = await owner.signIn({ ...credentials, totpCode: totp(credentials.totpSecret, loginTime) });
  assert.equal(user.role, 'owner');
  const targets = {};
  const servers = [];
  let project, group, releaseToken, deployToken, remote;
  const workers = [];
  async function enroll(name, scope) {
    const enrollment = await session.post('/api/auth/runner-enrollments', { name, scopes: [scope], projects: [project.id] });
    const issued = await session.post('/api/auth/runner-enroll', { code: enrollment.enrollmentCode });
    return issued.secret;
  }
  async function runnerRequest(token, route, data) {
    const response = await fetch(new URL(route, studio), { method: 'POST', headers: {
      Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', 'X-Client-Id': 'scenario-fixture',
    }, body: JSON.stringify(data), signal: AbortSignal.timeout(30000) });
    const text = await response.text();
    assert.ok(response.ok, route + ' HTTP ' + response.status + ' ' + text.slice(0, 300));
    return text ? JSON.parse(text) : null;
  }
  async function initProject() {
    cpSync('project-templates/static-web', repo, { recursive: true, filter: file => !['node_modules', 'dist'].includes(path.basename(file)) });
    git('init', '-q', '-b', 'develop');
    git('config', 'user.email', 'scenario@example.invalid');
    git('config', 'user.name', 'Scenario fixture');
    git('init', '--bare', '-q', path.join(workspace, 'counter-origin.git'));
    git('remote', 'add', 'origin', path.join(workspace, 'counter-origin.git'));
    git('add', '.'); git('commit', '-qm', 'Initialize the Studio static-web template');
    git('--git-dir', path.join(workspace, 'counter-origin.git'), 'config', 'http.receivepack', 'true');
    git('--git-dir', path.join(workspace, 'counter-origin.git'), 'symbolic-ref', 'HEAD', 'refs/heads/develop');
    git('push', '-qu', 'origin', 'develop');
    const serving = await serveGit(workspace);
    remote = serving.url;
    servers.push(serving.server);
    group = await session.post('/api/workspaces', { displayName: 'Department demo' });
    await session.put('/api/workspaces/' + group.id + '/quota', { modelTokensPerWeek: 1000000, computeCoreHoursPerWeek: 40, hostingSlots: 2 });
    project = await session.post('/api/projects', { workspaceId: group.id, displayName: projectName, repositoryPath: repo, repositoryUrl: remote });
    assert.equal(project.repositoryPath, repo);
    return project;
  }
  async function declare() {
    const declarations = path.join(repo, 'docs/deployments/environments');
    mkdirSync(declarations, { recursive: true });
    for (const kind of ['stage', 'production']) {
      const files = new Map();
      const server = http.createServer( (req, res) => {
        const file = files.get(req.url);
        res.writeHead(file ? 200 : 404, { 'Content-Type': req.url === '/' ? 'text/html' : 'text/plain' });
        res.end(file ?? 'File unavailable');
      });
      await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
      servers.push(server);
      const address = 'http://127.0.0.1:' + server.address().port;
      const healthUrl = address + (kind === 'production' ? '/health.txt' : '/');
      targets[kind] = { address, healthUrl, files };
      json(path.join(declarations, kind + '.json'), { id: kind, name: kind, kind, targetHostId: 'scenario-host',
        template: 'static', destination: path.join(workspace, kind), healthUrl, address, approval: 'person', secretRefs: [] });
    }
    appendFileSync(path.join(repo, '.agent-studio/project.yml'), '\ndelivery:\n  fullSuite: off\n');
    git('add', '.'); git('commit', '-qm', 'Declare two local static environments');
    git('push', '-q', 'origin', 'develop');
    releaseToken = await enroll('scenario-gate', 'runner.release');
    deployToken = await enroll('scenario-host', 'runner.deploy');
    return owner.environments(projectName);
  }
  async function publish(revision, cardKey) {
    cpSync('scenarios/application-lifecycle/click-counter.html', path.join(repo, 'index.html'));
    writeFileSync(path.join(repo, 'app.test.mjs'), "import assert from 'node:assert/strict';\nimport { readFileSync } from 'node:fs';\nassert.match(readFileSync('index.html', 'utf8'), /id=\"reset\"/);\n");
    const health = path.join(repo, 'health.txt');
    if (revision === 'fault') rmSync(health, { force: true });
    else writeFileSync(health, revision + '\n');
    if (revision === 'repair') appendFileSync(path.join(repo, 'app.test.mjs'), "assert.match(readFileSync('health.txt', 'utf8'), /repair/);\n");
    git('add', '.'); git('commit', '-qm', (cardKey ? cardKey + ' ' : '') + revision);
    const commit = git('rev-parse', 'HEAD');
    // The fixture gate produces evidence only after running this site's actual test and build.
    execFileSync(process.execPath, ['--test', 'app.test.mjs'], { cwd: repo });
    execFileSync(process.execPath, ['build.mjs'], { cwd: repo });
    if (existsSync(health)) cpSync(health, path.join(repo, 'dist/health.txt'));
    else rmSync(path.join(repo, 'dist/health.txt'), { force: true });
    git('push', '--atomic', '-q', 'origin', 'HEAD:refs/heads/develop', 'HEAD:refs/heads/main');
    const dir = path.join(process.env.Deploy__ReleaseEvidenceRoot, commit);
    mkdirSync(dir, { recursive: true });
    const archive = path.join(dir, 'release.tar.gz');
    execFileSync('tar', ['-czf', archive, '-C', path.join(repo, 'dist'), '.']);
    const digest = sha256(readFileSync(archive));
    const artifact = 'release-package@sha256:' + digest;
    // Same local gate receipt contract as deploy-staging-retry-production-approval.spec.ts.
    writeFileSync(path.join(dir, 'release.sha256'), digest);
    writeFileSync(path.join(dir, 'full-gate.log'), 'PROMOTION_FULL_GATE=passed\n');
    json(path.join(dir, 'promotion-record.json'), { status: 'promoted', gate: 'passed', candidateSha: commit,
      atomicPush: true, gateRunId: 'fixture/' + commit, releasePackage: { status: 'written', sha256: digest } });
    json(path.join(dir, 'deploy-artifact.json'), { project: projectName, artifact, commit, sourceSha256: digest });
    return runnerRequest(releaseToken, '/api/deploy/gate-releases', { project: projectName, commit, artifact });
  }
  async function applyDeployment(job, route) {
    assert.ok(job.kind === 'standard' && job.template === 'static' && !job.runbookName,
      'The fixture can only serve static deployments. Unsupported job ' + job.id);
    const target = targets[job.environmentId];
    assert.ok(target, 'Unknown fixture environment ' + job.environmentId);
    if (job.isStop) {
      target.files.clear();
      const stopped = await fetch(target.address);
      assert.equal(stopped.status, 404);
      return stopped.status;
    }
    const response = await fetch(new URL(route + '/artifact?content', studio), { headers: {
      Authorization: 'Bearer ' + deployToken, 'X-Deploy-Lease': job.leaseToken,
    } });
    assert.equal(response.status, 200, 'Download leased artifact');
    const archive = Buffer.from(await response.arrayBuffer());
    assert.equal('release-package@sha256:' + sha256(archive), job.artifact);
    target.files.clear();
    const files = execFileSync('tar', ['-tzf', '-'], { input: archive }).toString().split('\n');
    for (const [url, file] of [['/', './index.html'], ['/health.txt', './health.txt']]) {
      if (files.includes(file)) target.files.set(url, execFileSync('tar', ['-xzOf', '-', file], { input: archive }));
    }
    const health = await fetch(target.healthUrl);
    return health.status;
  }
  const deployNext = () => deployNextJob((route, data) => runnerRequest(deployToken, route, data), applyDeployment);
  async function deploy(kind, artifact) {
    const result = await deployNext();
    assert.ok(result, 'Studio offered no deployment job for scenario-host');
    assert.equal(result.environment, kind);
    assert.equal(result.artifact, artifact);
    return result;
  }
  return { user, owner, session, credentials, targets, loginTime, initProject, declare, publish, deploy, deployNext,
    get project() { return project; }, get group() { return group; },
    async startCoding() {
      for (const role of ['coding', 'review']) workers.push(await startWorker({ workspace, studio, project, session, remote }, role));
    },
    readiness: () => session.post(projectApi + '/execution-readiness-probe', { hostId: workers[0].id }),
    async admission() {
      const token = await enroll('scenario-quota', 'runner.claim');
      const response = await fetch(new URL('/api/quota/admission/' + encodeURIComponent(projectName), studio),
        { headers: { Authorization: 'Bearer ' + token } });
      assert.equal(response.status, 200, 'project-bound runner quota admission');
      return response.json();
    },
    approve: artifact => session.post(projectApi + '/environments/promotions/approve', { artifact }),
    async close() {
      for (const worker of workers) await worker.stop();
      for (const server of servers) { server.closeAllConnections(); server.close(); }
      await session.signOut();
    } };
}

export async function nextTotpWindow(loginTime) {
  const delay = Math.floor(loginTime / 30000) * 30000 + 31000 - Date.now();
  if (delay > 0) await new Promise(resolve => setTimeout(resolve, delay));
}
