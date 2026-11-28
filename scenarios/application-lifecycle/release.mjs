import { StepFailure } from '../../scripts/release/product-scenario/runner.mjs';

export async function runFixtureRelease(step, ctx, record, site) {
  const readEnvironments = () => ctx.owner.environments(ctx.state.project.name);
  async function deploymentView(kind, artifacts) {
    let view = await readEnvironments();
    const target = view.environments.find(item => item.declaration.kind === kind);
    const job = view.jobs.find(item => item.status === 'queued' && item.environmentId === target?.declaration.id
      && artifacts.includes(item.artifact));
    if (!job) return view;
    do {
      const result = await site.deployNext();
      view = await readEnvironments();
      if (!result) break;
      if (result.job === job.id) {
        record.note(kind + 'Job', result);
        break;
      }
    } while (view.jobs.some(item => item.id === job.id && item.status === 'queued'));
    return view;
  }

  if (ctx.state.preview) return step.run(ctx, record, { deploymentView });
  const release = await site.publish('approval-check');
  record.note('separateFixtureRelease', release.version);
  await step.run(ctx, record, { release, deploymentView });
  throw new StepFailure('The manager released a separate fixture artifact. No accepted wish release was observed.');
}
