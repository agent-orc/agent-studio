import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { buildRunnerSetupRequest, runnerSetupIssues, type RunnerSetupConfig } from './runner-setup.model';

const HOST: RemoteHost = {
  id: 'agent-runner-01',
  name: 'agent-runner-01',
  role: 'remote',
  address: 'agent-runner',
  clientId: 'runner-client-01',
  status: 'offline',
  os: 'Ubuntu 24.04 LTS',
  lastHeartbeatAt: null,
  uptimeLabel: null,
  capabilities: ['linux', 'dotnet 10'],
  cliQuotas: [],
  stats: null,
};

const VALID: RunnerSetupConfig = {
  sshTarget: 'agent-runner',
  taskServerUrl: 'https://tasks.example.test',
  connectionMode: 'central',
  clientId: 'runner-client-01',
  gitRemote: 'https://github.com/example/agent-studio.git',
  gitPushRemote: 'git@github.com:example/agent-studio.git',
};

describe('runner setup model', () => {
  it('requires every operator-owned connection value', () => {
    expect(runnerSetupIssues({
      sshTarget: '', taskServerUrl: '', connectionMode: '', clientId: '', gitRemote: '', gitPushRemote: '',
    })).toEqual([
      'SSH target is required.',
      'Task Server URL is required.',
      'Choose how the remote host reaches the Task Server.',
      'Client identity is required.',
      'Git remote URL is required.',
      'Git push URL is required.',
    ]);
  });

  it('blocks loopback for central/LAN access and permits it only through a tunnel', () => {
    const loopback = { ...VALID, taskServerUrl: 'http://localhost:5031', connectionMode: 'lan' as const };
    expect(runnerSetupIssues(loopback)).toContain(
      'A remote host cannot reach this loopback URL. Choose Tunnel or enter a central or LAN URL.',
    );
    expect(runnerSetupIssues({ ...loopback, connectionMode: 'tunnel' })).toEqual([]);
  });

  it('builds the exact idempotent remote setup and protected provider-auth contract', () => {
    const request = buildRunnerSetupRequest(HOST, VALID);

    expect(request.title).toBe('Set up agent host on agent-runner-01');
    expect(request.command).toContain('bash scripts/remote-runner-onboard.sh');
    expect(request.command).toContain("--host 'agent-runner'");
    expect(request.command).toContain("--server 'https://tasks.example.test'");
    expect(request.command).toContain("--topology 'central'");
    expect(request.command).toContain("--runner-name 'agent-runner-01'");
    expect(request.command).toContain("--git-remote 'https://github.com/example/agent-studio.git'");
    expect(request.command).toContain("--git-push-remote 'git@github.com:example/agent-studio.git'");
    expect(request.context).toMatchObject({
      taskServerUrl: 'https://tasks.example.test',
      clientId: 'runner-client-01',
      gitRemote: 'https://github.com/example/agent-studio.git',
      gitPushRemote: 'git@github.com:example/agent-studio.git',
    });
    expect(request.prompt).toContain('Reachability gate (must run first)');
    expect(request.prompt).toContain('CodingAgentRunner');
    expect(request.prompt).toContain('[0.5.0,)');
    expect(request.prompt).toContain('systemd');
    expect(request.prompt).toContain('agent-host.service');
    expect(request.prompt).toContain('Preserve the existing runner identity `agent-runner-01`');
    expect(request.prompt).toContain('Authentication belongs to this host and runner user');
    expect(request.prompt).toContain('provider re-auth action in Execution Hosts');
    expect(request.prompt).toContain('claude auth status --text');
    expect(request.prompt).toContain('codex login status');
    expect(request.prompt).toContain('never print credential-file contents or token values');
    expect(request.prompt).not.toContain('codex login --device-auth');
    expect(request.prompt).not.toContain('claude auth login');
    expect(request.prompt).toContain('one real smoke task');
  });
});
