const DEFAULT_PROJECT = 'Agent Task Processor';
const DEFAULT_CLIENT_ID = 'local-default';

export const BACKFILL = Object.freeze([
  Object.freeze({
    taskId: 'AGT-2298',
    sha: '2a25bd3a46cde65a1dbe9e2f357b14c029bfd1b9',
  }),
  Object.freeze({
    taskId: 'AGT-2300',
    sha: 'ff982d2981209fa92726037053e566f5045ce643',
  }),
  Object.freeze({
    taskId: 'AGT-2320',
    sha: '848c11acb50cf7311d3604ac3e1e0755f6155ed1',
  }),
  Object.freeze({
    taskId: 'AGT-2321',
    sha: 'e36ee91e6ca9909a7fbe5686fdfffdacb7d52f58',
  }),
]);

function option(args, name, fallback) {
  const index = args.indexOf(name);
  if (index < 0) return fallback;
  const value = args[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`${name} requires a value`);
  return value;
}

export function parseArgs(args, env = process.env) {
  return {
    apply: args.includes('--apply'),
    baseUrl: option(args, '--base-url', env.TASKBOARD_BASE_URL)?.replace(/\/+$/, ''),
    project: option(args, '--project', env.TASKBOARD_PROJECT ?? DEFAULT_PROJECT),
    clientId: option(args, '--client-id', env.TASKBOARD_CLIENT_ID ?? DEFAULT_CLIENT_ID),
  };
}

function printPlan(write) {
  write('AGT-2326 commit-attribution backfill plan:');
  for (const item of BACKFILL)
    write(`  ${item.taskId} -> ${item.sha}`);
}

async function responseBody(response) {
  const text = await response.text();
  if (!text) return '';
  try {
    return JSON.stringify(JSON.parse(text));
  } catch {
    return text;
  }
}

export async function runBackfill(options, dependencies = {}) {
  const fetchImpl = dependencies.fetchImpl ?? globalThis.fetch;
  const write = dependencies.write ?? console.log;

  printPlan(write);
  if (!options.apply) {
    write('Dry run only. Pass --apply and --base-url after the API is deployed.');
    return { applied: 0, failed: 0, dryRun: true };
  }
  if (!options.baseUrl)
    throw new Error('--base-url or TASKBOARD_BASE_URL is required with --apply');
  if (!options.project?.trim()) throw new Error('A project name is required');
  if (!options.clientId?.trim()) throw new Error('A client id is required');

  const headers = {
    'X-Client-Id': options.clientId,
    ...(process.env.AGENT_STUDIO_API_TOKEN
      ? { Authorization: `Bearer ${process.env.AGENT_STUDIO_API_TOKEN}` } : {}),
  };
  const watchPathsResponse = await fetchImpl(`${options.baseUrl}/api/watch-paths`, { headers });
  if (!watchPathsResponse.ok)
    throw new Error(`watch-path lookup failed: ${watchPathsResponse.status} ${await responseBody(watchPathsResponse)}`);

  const watchPaths = await watchPathsResponse.json();
  const matches = watchPaths.filter(entry =>
    entry?.path
    && entry?.name?.localeCompare(options.project, undefined, { sensitivity: 'accent' }) === 0);
  if (matches.length !== 1)
    throw new Error(`Expected exactly one canonical watchPath for project "${options.project}", found ${matches.length}`);
  const watchPath = matches[0].path;

  let applied = 0;
  const failures = [];
  for (const item of BACKFILL) {
    const url = `${options.baseUrl}/api/tasks/${encodeURIComponent(item.taskId)}/commits`
      + `?watchPath=${encodeURIComponent(watchPath)}`;
    const response = await fetchImpl(url, {
      method: 'PUT',
      headers: {
        ...headers,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ commits: [item.sha] }),
    });
    const body = await responseBody(response);
    if (response.ok) {
      applied++;
      write(`OK ${item.taskId} -> ${item.sha}`);
    } else {
      failures.push({ ...item, status: response.status, body });
      write(`FAILED ${item.taskId}: ${response.status} ${body}`);
    }
  }

  write(`Backfill result: ${applied} succeeded, ${failures.length} failed.`);
  return { applied, failed: failures.length, failures, dryRun: false };
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const result = await runBackfill(options);
  if (result.failed > 0) process.exitCode = 1;
}

if (import.meta.url === new URL(process.argv[1], 'file:').href) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
