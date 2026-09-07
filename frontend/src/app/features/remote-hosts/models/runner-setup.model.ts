import type { VisibleCliTaskRequest } from '../../visible-cli-task';
import { isLocalUrl } from '../../task-server';
import type { RemoteHost } from './remote-host.model';

export type RunnerSetupConnectionMode = 'central' | 'lan' | 'tunnel';

export interface RunnerSetupConfig {
  sshTarget: string;
  taskServerUrl: string;
  connectionMode: RunnerSetupConnectionMode | '';
  clientId: string;
  gitRemote: string;
  gitPushRemote: string;
}

/** Validate the operator-owned values before a provisioning task can be queued. */
export function runnerSetupIssues(config: RunnerSetupConfig): string[] {
  const issues: string[] = [];
  if (!config.sshTarget.trim()) issues.push('SSH target is required.');
  if (!config.taskServerUrl.trim()) {
    issues.push('Task Server URL is required.');
  } else if (!isHttpUrl(config.taskServerUrl)) {
    issues.push('Task Server URL must be an absolute HTTP or HTTPS URL.');
  }
  if (!config.connectionMode) issues.push('Choose how the remote host reaches the Task Server.');
  if (!config.clientId.trim()) issues.push('Client identity is required.');
  if (!config.gitRemote.trim()) issues.push('Git remote URL is required.');
  if (!config.gitPushRemote.trim()) issues.push('Git push URL is required.');
  if (config.taskServerUrl.trim() && isLocalUrl(config.taskServerUrl) && config.connectionMode !== 'tunnel') {
    issues.push('A remote host cannot reach this loopback URL. Choose Tunnel or enter a central or LAN URL.');
  }
  return issues;
}

/** Build the durable CLI task that owns progress, operator input, and history. */
export function buildRunnerSetupRequest(host: RemoteHost, config: RunnerSetupConfig): VisibleCliTaskRequest {
  const sshTarget = config.sshTarget.trim();
  const taskServerUrl = config.taskServerUrl.trim().replace(/\/+$/, '');
  const clientId = config.clientId.trim();
  const gitRemote = config.gitRemote.trim();
  const gitPushRemote = config.gitPushRemote.trim();
  const connectionMode = config.connectionMode || 'not selected';
  const healthUrl = `${taskServerUrl}/healthz`;
  const controllerCommand = [
    'bash scripts/remote-runner-onboard.sh',
    `--host ${shellArg(sshTarget)}`,
    `--server ${shellArg(taskServerUrl)}`,
    `--topology ${shellArg(connectionMode)}`,
    `--client-id ${shellArg(clientId)}`,
    `--runner-name ${shellArg(host.name)}`,
    `--git-remote ${shellArg(gitRemote)}`,
    `--git-push-remote ${shellArg(gitPushRemote)}`,
  ].join(' ');

  return {
    title: `Set up agent host on ${host.name}`,
    scope: `Set up the agent host daemon on ${host.name}`,
    reason: 'Provision the host idempotently, verify its Studio-provisioned provider environment, register its agent-host daemon, and prove one real remote task handoff.',
    command: controllerCommand,
    expectedDuration: '10 to 20 minutes',
    cliType: 'codex',
    context: {
      host: host.name,
      sshTarget,
      taskServerUrl,
      connectionMode,
      clientId,
      gitRemote,
      gitPushRemote,
      executionBoundary: 'Run the controller agent locally; perform every host operation through SSH.',
    },
    prompt: [
      `Set up remote host ${host.name}. Run every inspection and mutation through SSH target \`${sshTarget}\`; do not install or configure agent-host on the operator workstation.`,
      '',
      'Treat this as an idempotent remote-host process that is safe to repeat after a wipe. Report each phase in the task conversation and fail with a concrete recovery instruction instead of waiting silently.',
      '',
      '1. Reachability gate (must run first)',
      `- From the remote host, verify \`${healthUrl}\` with curl and the exact header \`X-Client-Id: ${clientId}\`.`,
      `- Connection mode is \`${connectionMode}\`. If it is \`tunnel\`, verify the tunnel on the remote host before curl.`,
      '- Do not install or start agent-host until this remote curl succeeds. On failure, show whether the operator needs a central URL, LAN binding, or tunnel.',
      '',
      '2. Agent host and source setup',
      `- Run the product-owned controller exactly as recorded in the Execution contract: \`${controllerCommand}\`. Do not replace it with hand-written local installation steps.`,
      '- Confirm .NET 10 is available.',
      '- Install or update the NuGet global tool `CodingAgentRunner` with a version range of `[0.5.0,)`; verify the resolved version is at least 0.5.0.',
      '- The controller intentionally fails if the published NuGet package is not a DotnetTool. Report that packaging failure; do not bypass it with a session process or copied binary.',
      `- Configure agent-host with Task Server URL \`${taskServerUrl}\` and client identity \`${clientId}\` so every request sends that exact X-Client-Id.`,
      `- Preserve the existing runner identity \`${host.name}\`; do not rename historical runner ids during the daemon migration.`,
      `- Configure fetches from \`${gitRemote}\` and pushes through the host-owned write identity at \`${gitPushRemote}\`; do not transfer workstation credentials.`,
      '- Create or update `agent-host.service`, keep the `agent-runner.service` compatibility alias, run daemon-reload, enable it, and start it through systemd. Never leave agent-host as a shell, tmux, nohup, or user-session process.',
      '',
      '3. Provider authentication contract',
      '- Install Codex and Claude CLI on the host when missing, then report their versions.',
      '- Authentication belongs to this host and runner user. Never request, copy, repeat, or place an operator-device credential in this task conversation, command arguments, repository, task files, or host setup.',
      '- If either CLI is logged out, finish host installation without claim eligibility and direct the operator to the provider re-auth action in Execution Hosts. That browser flow runs the CLI on this host and stores the resulting session only in its native user store.',
      '- Treat `claude auth status --text` and `codex login status` as authoritative. Report each `runner-provider-auth` result and matching capability detail, but never print credential-file contents or token values.',
      '',
      '4. Verification and real handoff',
      '- Confirm the systemd service is active and enabled, the Task Server accepts the configured client identity, and the client registry shows a fresh LastSeen plus runnerGitStatus `ready`.',
      '- Run the agent-host connection or health check and include the result.',
      '- Queue and complete one real smoke task through this remote host. Prove the remote lease/runner attribution and final task result; a local or synthetic no-op is not acceptance.',
      '- Finish with the installed agent-host version, service state, Task Server reachability result, provider-auth probe results, refreshed LastSeen, and smoke-task key/result.',
    ].join('\n'),
  };
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function shellArg(value: string): string {
  return `'${value.replace(/'/g, `'"'"'`)}'`;
}
