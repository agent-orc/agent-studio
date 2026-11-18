import { telemetryEnabled } from './telemetry-config.mjs';

/**
 * Dependencies the health check probes, from HEALTH_DEPENDENCIES:
 * comma-separated `name=url` pairs such as `orders=http://orders:8080/health`.
 */
export function dependenciesFromEnv(env = process.env) {
  return (env.HEALTH_DEPENDENCIES ?? '').split(',').map(item => item.trim()).filter(Boolean).map(item => {
    const separator = item.indexOf('=');
    if (separator < 1) throw new Error(`HEALTH_DEPENDENCIES entry "${item}" must be name=url.`);
    const name = item.slice(0, separator).trim();
    const url = new URL(item.slice(separator + 1).trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error(`Dependency ${name} must use http or https.`);
    return { name, url: url.toString() };
  });
}

async function probe(dependency, fetchImpl, timeoutMs) {
  const started = performance.now();
  try {
    const response = await fetchImpl(dependency.url, { signal: AbortSignal.timeout(timeoutMs) });
    const latencyMs = Math.round(performance.now() - started);
    return response.ok
      ? { name: dependency.name, status: 'ok', latencyMs }
      : { name: dependency.name, status: 'down', latencyMs, detail: `HTTP ${response.status}` };
  } catch (error) {
    return { name: dependency.name, status: 'down', latencyMs: Math.round(performance.now() - started),
      detail: error?.name === 'TimeoutError' ? `No answer within ${timeoutMs} ms` : 'Unreachable' };
  }
}

/**
 * The health report: `ok` when every dependency answers, `down` (HTTP 503)
 * when one does not. Telemetry is reported but never fails the check, so a
 * local run without Studio stays healthy.
 */
export async function checkHealth({
  dependencies = dependenciesFromEnv(), fetchImpl = fetch, timeoutMs = 2000, env = process.env,
} = {}) {
  const results = await Promise.all(dependencies.map(dependency => probe(dependency, fetchImpl, timeoutMs)));
  const checks = [
    { name: 'process', status: 'ok', uptimeSeconds: Math.round(process.uptime()), memoryBytes: process.memoryUsage().rss },
    { name: 'telemetry', status: telemetryEnabled(env) ? 'ok' : 'disabled' },
    ...results,
  ];
  return { status: results.some(item => item.status === 'down') ? 'down' : 'ok', checks };
}
