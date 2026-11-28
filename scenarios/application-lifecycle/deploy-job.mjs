// A host-wide claim can grant a different job from the one the scenario is waiting for.
export async function deployNextJob(request, apply) {
  const job = await request('/api/deploy/jobs/claim', { hostId: 'scenario-host' });
  if (!job) return null;
  const route = '/api/deploy/jobs/' + encodeURIComponent(job.project) + '/' + encodeURIComponent(job.id);
  const report = (healthy, evidence) => request(route + '/report', { leaseToken: job.leaseToken, healthy, evidence });
  let status;
  try {
    status = await apply(job, route);
  } catch (error) {
    await report(false, 'deploy-step-failed');
    throw error;
  }
  const healthy = job.isStop ? status === 404 : status >= 200 && status < 300;
  await report(healthy, healthy ? 'health-ok' : 'health-http-failed');
  return { job: job.id, environment: job.environmentId, artifact: job.artifact, healthStatus: status };
}
