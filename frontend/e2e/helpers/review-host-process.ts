import { spawn } from 'node:child_process';
import { closeSync, existsSync, openSync, readdirSync, readFileSync, readlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * AGT-3690: kills and restarts the first-run profile's real review daemon
 * (`first-run-review`), the way the operator crisis playthrough killed its
 * review host mid-review. The daemon, its detached review worker and the
 * worker's CLI share the daemon's environment, so `/proc/<pid>/environ` names
 * every process that belongs to this isolated review host and nothing else.
 */
export interface ReviewHostLaunch {
  command: string[];
  cwd: string;
  env: Record<string, string>;
  /** Every process the kill hit: daemon, worker, CLI. */
  killed: number[];
}

interface HostProcess {
  pid: number;
  command: string[];
  env: Record<string, string>;
}

function readNulList(path: string): string[] {
  try {
    return readFileSync(path, 'utf8').split('\0').filter(part => part.length > 0);
  } catch {
    return [];
  }
}

function reviewHostProcesses(workspace: string): HostProcess[] {
  const stateDir = `${workspace}/review-state`;
  const processes: HostProcess[] = [];
  for (const entry of readdirSync('/proc')) {
    const pid = Number(entry);
    if (!Number.isInteger(pid) || pid === process.pid) continue;
    const environ = readNulList(`/proc/${pid}/environ`);
    const command = readNulList(`/proc/${pid}/cmdline`);
    const ownsReviewState = environ.includes(`RUNNER_STATE_DIR=${stateDir}`);
    const runsInReviewWorkspace = command.some(part => part.includes(`${workspace}/review-attempts`)
      || part.includes(`${workspace}/bin/review/`));
    if (!ownsReviewState && !runsInReviewWorkspace) continue;
    const env: Record<string, string> = {};
    for (const pair of environ) {
      const split = pair.indexOf('=');
      if (split > 0) env[pair.slice(0, split)] = pair.slice(split + 1);
    }
    processes.push({ pid, command, env });
  }
  return processes;
}

function isDaemon(candidate: HostProcess): boolean {
  return candidate.env['RUNNER_ID'] === 'first-run-review'
    && candidate.command.includes('--poll')
    && candidate.command.some(part => part.endsWith('agent-host.dll'));
}

/**
 * SIGKILLs the review daemon, its detached worker and the worker's CLI, and
 * returns what is needed to start the same daemon again: same identity, same
 * state directory, so the replacement finds the persisted review slot.
 */
export function killReviewHost(workspace: string): ReviewHostLaunch {
  const processes = reviewHostProcesses(workspace);
  const daemon = processes.find(isDaemon);
  if (!daemon) throw new Error(`No first-run-review daemon runs for ${workspace}.`);
  const launch: ReviewHostLaunch = {
    command: daemon.command,
    cwd: readlinkSync(`/proc/${daemon.pid}/cwd`),
    env: daemon.env,
    killed: [],
  };
  // The detached worker is the daemon's child until the daemon dies, and its
  // CLI is the worker's child. Killing only the CLI lets the worker report a
  // tool failure and the replacement adopt it, which is not a lost review.
  const victims = new Set([...descendantsOf(daemon.pid), ...processes.map(host => host.pid)]);
  for (const pid of victims) {
    try { process.kill(pid, 'SIGKILL'); } catch { /* already gone */ }
  }
  writeFileSync(join(workspace, LAUNCH_FILE), JSON.stringify(launch), { mode: 0o600 });
  return { ...launch, killed: [...victims] };
}

const LAUNCH_FILE = 'review-host-launch.json';

/**
 * Starts the review daemon again when an earlier try of the spec stopped its
 * replacement, so a retry finds a review host. Returns the started pid, or
 * null when a daemon already runs.
 */
export function ensureReviewHost(workspace: string): number | null {
  if (reviewHostProcesses(workspace).some(isDaemon)) return null;
  const saved = join(workspace, LAUNCH_FILE);
  if (!existsSync(saved)) throw new Error(`No first-run-review daemon runs for ${workspace}.`);
  return startReviewHost(JSON.parse(readFileSync(saved, 'utf8')) as ReviewHostLaunch, workspace);
}

function descendantsOf(root: number): number[] {
  const children = new Map<number, number[]>();
  for (const entry of readdirSync('/proc')) {
    const pid = Number(entry);
    if (!Number.isInteger(pid)) continue;
    let stat: string;
    try { stat = readFileSync(`/proc/${pid}/stat`, 'utf8'); } catch { continue; }
    // The command name may contain spaces; the parent pid follows its closing parenthesis.
    const parent = Number(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[1]);
    children.set(parent, [...(children.get(parent) ?? []), pid]);
  }
  const found: number[] = [];
  const pending = [root];
  while (pending.length > 0) {
    const pid = pending.pop()!;
    found.push(pid);
    pending.push(...(children.get(pid) ?? []));
  }
  return found;
}

/** True once no killed process runs any more; a zombie waiting for its parent counts as gone. */
export function reviewHostGone(launch: ReviewHostLaunch): boolean {
  return launch.killed.every(pid => {
    try {
      const stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
      return stat.slice(stat.lastIndexOf(')') + 2).startsWith('Z');
    } catch {
      return true;
    }
  });
}

/** Starts the replacement daemon detached, appending to the profile's review runner log. */
export function startReviewHost(launch: ReviewHostLaunch, workspace: string): number {
  const log = openSync(join(workspace, 'review-runner.log'), 'a');
  const [command, ...args] = launch.command;
  const child = spawn(command, args, {
    cwd: launch.cwd,
    env: launch.env,
    detached: true,
    stdio: ['ignore', log, log],
  });
  closeSync(log);
  child.unref();
  if (!child.pid) throw new Error('The replacement review daemon did not start.');
  return child.pid;
}

/** Stops a replacement daemon this spec started; the profile teardown does not know it. */
export function stopReviewHost(pid: number | null): void {
  if (!pid) return;
  try { process.kill(pid, 'SIGTERM'); } catch { /* already gone */ }
}
