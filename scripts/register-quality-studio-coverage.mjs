#!/usr/bin/env node

// Register the coverage reports produced by this checkout's test gates.
// Usage: node scripts/register-quality-studio-coverage.mjs <base-url> [repo-id]
const [baseUrl, requestedId] = process.argv.slice(2);
if (!baseUrl) {
  console.error('Usage: node scripts/register-quality-studio-coverage.mjs <base-url> [repo-id]');
  process.exitCode = 2;
} else {
  try {
    const root = new URL(baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`);
    if (!['http:', 'https:'].includes(root.protocol)) throw new Error('Base URL must use HTTP or HTTPS.');
    const headers = { 'Content-Type': 'application/json' };
    if (process.env.QUALITY_STUDIO_API_TOKEN) {
      headers.Authorization = `Bearer ${process.env.QUALITY_STUDIO_API_TOKEN}`;
    }
    if (process.env.QUALITY_STUDIO_CLIENT_ID) {
      headers['X-Client-Id'] = process.env.QUALITY_STUDIO_CLIENT_ID;
    }
    const list = await fetch(new URL('api/repos', root), {
      headers, signal: AbortSignal.timeout(15_000),
    });
    if (!list.ok) throw new Error(`Quality Studio returned ${list.status} while listing repositories.`);
    const registrations = (await list.json()).repositories;
    const normalizePath = (value) => {
      const normalized = value.replaceAll('\\', '/').replace(/\/$/, '');
      return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
    };
    const checkout = normalizePath(process.cwd());
    const registration = registrations.find((item) =>
      normalizePath(item.rootPath) === checkout &&
      (!requestedId || item.id === requestedId));
    if (!registration) throw new Error('No Quality Studio registration matches this exact checkout.');

    const reportPaths = 'coverage/dotnet/**/*.cobertura.xml;coverage/frontend/lcov.info';
    const sensors = [...(registration.sensors ?? [])];
    const index = sensors.findIndex((sensor) => sensor.id === 'coverage');
    const coverage = { id: 'coverage', enabled: true, configuration: { reportPaths } };
    if (index < 0) sensors.push(coverage);
    else sensors[index] = coverage;
    const request = {
      id: registration.id,
      displayName: registration.displayName,
      rootPath: registration.rootPath,
      globalInputsDirectory: registration.globalInputsDirectory,
      inputBudgetCharacters: registration.inputBudgetCharacters,
      enabledReviewKinds: registration.enabledReviewKinds,
      sensors,
      defaultReviewTokenCap: registration.defaultReviewTokenCap,
      defaultReviewCostCap: registration.defaultReviewCostCap,
    };
    const update = await fetch(new URL(`api/repos/${encodeURIComponent(registration.id)}`, root), {
      method: 'PUT', headers, body: JSON.stringify(request), signal: AbortSignal.timeout(15_000),
    });
    if (!update.ok) throw new Error(`Quality Studio returned ${update.status} while updating the registration.`);
    console.log(`Registered coverage report paths for ${registration.id}: ${reportPaths}`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
