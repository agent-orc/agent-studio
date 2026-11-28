// AGT-3183 uses its delivered Playwright steps on a healthy local release.
// If the installation cannot deliver a reviewed card, exercise the remaining
// controls as their actual users and keep the unmet dependency in the verdict.
import assert from 'node:assert/strict';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';
import { playScenario } from '../../scripts/release/product-scenario/play.mjs';
import { parseConfig } from '../../scripts/release/product-scenario/config.mjs';
import { StepFailure } from '../../scripts/release/product-scenario/runner.mjs';
import { installation, nextTotpWindow, projectName, studio, workspace } from './installation.mjs';

const site = await installation();
const config = parseConfig(['--studio-url', studio, '--user-file', path.join(workspace, 'owner.json'),
  '--platform', 'local fixture', '--poll-seconds', '2', '--coding-timeout-minutes', '1',
  '--deploy-timeout-minutes', '0.25', '--fix-timeout-minutes', '0.25', '--release-timeout-minutes', '0.25',
  '--report-dir', path.join(process.env.JOB_RESULTS_DIR, 'product')], process.env);
const actorId = { Owner: 'owner', 'Product manager': 'pm', Designer: 'designer', Manager: 'manager' };

function prepareSteps(steps) {
  return steps.map(step => ({
    ...step,
    // These controls can be inspected even without a reviewed delivery.
    needs: ['delivery', 'feedback', 'accept', 'release'].includes(step.id) ? ['invite', 'live'] : step.needs,
    async run(ctx, record) {
      const page = await ctx.browser.page(actorId[step.actor] ?? 'owner');
      try {
        if (step.id === 'delivery' && !ctx.state.card) {
          await page.openAddress(site.targets.stage.address);
          const cards = await site.owner.tasks(site.project.id);
          record.note('cards', cards.map(c => ({ key: c.key, title: c.title, lane: c.state })));
          throw new StepFailure('Studio has no confirmed wish card to review. Opened the live stage as the preview substitute.');
        }
        if (step.id === 'feedback' && !ctx.state.preview) {
          await page.openAddress(site.targets.stage.address);
          const crop = await page.buttonCrop('Reset');
          const cropFile = 'screenshots/designer-reset-crop.png';
          await writeFile(path.join(config.reportDir, cropFile), crop);
          record.screenshot(cropFile, 'Reset button selected on stage');
          record.screenshot(await page.screenshot('marked-control'), 'Stage with the control the designer selected');
          const form = new FormData();
          form.append('comment', config.feedback);
          form.append('selector', '#reset');
          form.append('previewAddress', site.targets.stage.address);
          form.append('crop', new Blob([crop], { type: 'image/png' }), 'reset.png');
          // Submit only to the wish card. If the chat never created it, record the missing target.
          if (!ctx.state.card) throw new StepFailure('Captured the live reset button. The designer has no confirmed card to receive feedback.');
          const response = await ctx.people.get('designer').api.session.send('POST',
            '/api/tasks/' + encodeURIComponent(ctx.state.card.id) + '/preview-feedback',
            { query: { project: site.project.id }, form });
          record.note('feedback', { status: response.status, answer: response.body, comment: config.feedback, cropBytes: crop.length });
          throw new StepFailure('Submitted a crop of the live stage as the designer. HTTP ' + response.status + '. No reviewed card preview or measured fix delivery.');
        }
        if (step.id === 'accept' && !ctx.state.preview) {
          await page.openProjectUrl(projectName, ctx.state.projectUrlId);
          if (ctx.state.card) await page.select('product-card-select', ctx.state.card.id);
          const status = await page.testIdState('product-status', 1000);
          const button = await page.testIdState('product-accept', 1000);
          record.note('controls', { status, acceptVisible: button.visible });
          throw new StepFailure('No reviewed delivery is available for acceptance.');
        }
        if (step.id === 'release' && !ctx.state.preview) {
          const release = await site.publish('approval-check');
          const artifact = release.artifactDigests[0];
          const result = await site.deploy('stage', artifact);
          assert.equal(result.healthStatus, 200);
          await page.openAddress(site.targets.stage.address);
          const pm = await ctx.people.get('pm').api.approvePromotion(projectName, artifact);
          const manager = await ctx.people.get('manager').api.approvePromotion(projectName, artifact);
          record.note('approvalProbe', { version: release.version, artifact, pm: pm.status, manager: manager.status, managerAnswer: manager.body });
          if (manager.status === 200) record.note('production', await site.deploy('production', artifact));
          throw new StepFailure('Approval was tried on a separate staged fixture release. No accepted wish release. PM HTTP ' + pm.status + ', manager HTTP ' + manager.status);
        }
        try {
          await step.run(ctx, record);
        } catch (error) {
          if (step.id !== 'retire-card' || !ctx.state.card || error.code !== 'integration-dead-end') throw error;
          const { id, key } = ctx.state.card;
          const reason = 'Clean up the unfinished card in this disposable scenario after recording its delivery outcome.';
          await ctx.owner.moveTask(id, site.project.id, '7-archive', reason);
          record.note('archiveOverride', { key, reason, originalRefusal: error.status });
          await step.run(ctx, record);
        }
      } finally {
        if (step.id === 'delivery') {
          record.note('runnerReadiness', await site.readiness());
          if (ctx.state.card) {
            const card = await site.owner.task(ctx.state.card.id, site.project.id);
            record.note('delivery', { key: card.key, lane: card.state, preview: card.preview ?? null });
          }
        }
        if (!record.screenshots.length) {
          if (page === await ctx.browser.page('owner') && ctx.owner.session.signedIn) {
            await page.useSession(ctx.owner.session);
            await page.openAddress(studio + (step.id === 'live' ? '/api/projects/Click%20Counter/environments' : '/api/projects'));
          }
          record.screenshot(await page.screenshot(step.id), (step.actor ?? 'Owner') + ' at the end of ' + step.id);
        }
      }
    },
  }));
}
try {
  await site.initProject();
  await site.declare();
  const baseline = await site.publish('feature');
  const artifact = baseline.artifactDigests[0];
  assert.equal((await site.deploy('stage', artifact)).healthStatus, 200);
  await site.approve(artifact);
  assert.equal((await site.deploy('production', artifact)).healthStatus, 200);
  await site.startCoding();
  await nextTotpWindow(site.loginTime);
  const result = await playScenario(config, { prepareSteps, log: console.log });
  process.exitCode = result.status === 'passed' && result.teardownStatus === 'passed' ? 0 : 1;
} finally {
  await site.close();
}
