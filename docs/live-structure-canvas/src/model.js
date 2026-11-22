// Facts, recorded deltas and persona view specifications for the AGT-3955
// prototype. Roles only: no host names, addresses or people.
// Real facts from the card and the code are marked `real`; every other value
// is illustrative and modelled on the installation.
(function (root) {
  'use strict';

  const AS_OF = '2026-11-17T21:40:00Z';
  const GB = 1024 ** 3;

  // Placement and connectivity, as the extended structure endpoint (AGT-3954) would return them.
  const nodes = [
    { id: 'outside', kind: 'outside', kicker: 'Outside the installation', label: 'Outside services', cols: 5 },
    { id: 'ext-cdn', parent: 'outside', kind: 'external', label: 'CDN proxy', sub: 'Public entry, HTTPS' },
    { id: 'ext-github', parent: 'outside', kind: 'external', label: 'GitHub', sub: 'Git remotes' },
    { id: 'ext-registry', parent: 'outside', kind: 'external', label: 'Container registry', sub: 'Release images' },
    { id: 'ext-models', parent: 'outside', kind: 'external', label: 'Model providers', sub: 'Model APIs' },
    { id: 'tgt-offhost', parent: 'outside', kind: 'target', label: 'Off-host backup target', sub: 'Not configured', ghost: true },

    { id: 'site-cloud', kind: 'site', kicker: 'Site', label: 'Cloud', row: true },
    { id: 'm-studio', parent: 'site-cloud', kind: 'machine', label: 'Studio server', sub: 'Cloud server · software RAID 1', machineKind: 'cloud-server' },
    { id: 'c-edge', parent: 'm-studio', kind: 'container', label: 'edge', sub: 'Reverse proxy, TLS', role: 'edge' },
    { id: 'c-web', parent: 'm-studio', kind: 'container', label: 'web', sub: 'Studio web app', role: 'web' },
    { id: 'c-api', parent: 'm-studio', kind: 'container', label: 'api', sub: 'Studio API, task authority', role: 'api' },
    { id: 'c-engine', parent: 'm-studio', kind: 'container', label: 'engine', sub: 'Orchestrator engine', role: 'engine' },
    { id: 'c-runner-a', parent: 'm-studio', kind: 'container', label: 'runner daemon A', sub: 'Coding host', role: 'runner' },
    { id: 'c-runner-b', parent: 'm-studio', kind: 'container', label: 'runner daemon B', sub: 'Coding host', role: 'runner' },

    { id: 'site-lab', kind: 'site', kicker: 'Site', label: 'Home lab' },
    { id: 'm-lab', parent: 'site-lab', kind: 'machine', label: 'Home-lab machine', sub: 'Physical machine', machineKind: 'physical', cols: 1, openAt: 150 },
    { id: 'vm-lab', parent: 'm-lab', kind: 'vm', label: 'Linux VM', sub: 'Virtual machine', machineKind: 'vm' },
    { id: 'c-review-1', parent: 'vm-lab', kind: 'container', label: 'review host 1', sub: 'Review host', role: 'review' },
    { id: 'c-review-2', parent: 'vm-lab', kind: 'container', label: 'review host 2', sub: 'Review host', role: 'review' },
    { id: 'c-review-3', parent: 'vm-lab', kind: 'container', label: 'review host 3', sub: 'Review host', role: 'review' },
    { id: 'c-gate', parent: 'vm-lab', kind: 'container', label: 'gate host', sub: 'Batch gate host', role: 'gate' },
    { id: 'c-forwarder', parent: 'vm-lab', kind: 'container', label: 'link forwarder', sub: 'Tunnel endpoint', role: 'forwarder' },
  ];

  // Link kinds: cdn, https (to outside services), tunnel, forwarder, private (container network), backup, copy.
  const links = [
    { id: 'l-cdn', from: 'ext-cdn', to: 'c-edge', kind: 'cdn', label: 'CDN proxy', traffic: 182e3, latencyMs: 24 },
    { id: 'l-edge-web', from: 'c-edge', to: 'c-web', kind: 'private', label: 'Container network', traffic: 64e3, latencyMs: 1 },
    { id: 'l-edge-api', from: 'c-edge', to: 'c-api', kind: 'private', label: 'Container network', traffic: 121e3, latencyMs: 1 },
    { id: 'l-api-engine', from: 'c-api', to: 'c-engine', kind: 'private', label: 'Container network', traffic: 38e3, latencyMs: 1 },
    { id: 'l-runa-api', from: 'c-runner-a', to: 'c-api', kind: 'private', label: 'Claims and results', traffic: 29e3, latencyMs: 1 },
    { id: 'l-runb-api', from: 'c-runner-b', to: 'c-api', kind: 'private', label: 'Claims and results', traffic: 22e3, latencyMs: 1 },
    { id: 'l-tunnel', from: 'c-forwarder', to: 'c-api', kind: 'tunnel', label: 'Tunnel', traffic: 214e3, latencyMs: 31 },
    { id: 'l-fwd-gate', from: 'c-gate', to: 'c-forwarder', kind: 'forwarder', label: 'Forwarder', traffic: 96e3, latencyMs: 1 },
    { id: 'l-fwd-r1', from: 'c-review-1', to: 'c-forwarder', kind: 'forwarder', label: 'Forwarder', traffic: 41e3, latencyMs: 1 },
    { id: 'l-fwd-r2', from: 'c-review-2', to: 'c-forwarder', kind: 'forwarder', label: 'Forwarder', traffic: 47e3, latencyMs: 1 },
    { id: 'l-fwd-r3', from: 'c-review-3', to: 'c-forwarder', kind: 'forwarder', label: 'Forwarder', traffic: 12e3, latencyMs: 1 },
    { id: 'l-runa-models', from: 'c-runner-a', to: 'ext-models', kind: 'https', label: 'HTTPS', traffic: 26e3, latencyMs: 210 },
    { id: 'l-runb-models', from: 'c-runner-b', to: 'ext-models', kind: 'https', label: 'HTTPS', traffic: 19e3, latencyMs: 230 },
    { id: 'l-rev-models', from: 'c-review-2', to: 'ext-models', kind: 'https', label: 'HTTPS', traffic: 31e3, latencyMs: 190 },
    { id: 'l-api-github', from: 'c-api', to: 'ext-github', kind: 'https', label: 'HTTPS, git push', traffic: 6e3, latencyMs: 38 },
    { id: 'l-runa-github', from: 'c-runner-a', to: 'ext-github', kind: 'https', label: 'HTTPS, git', traffic: 9e3, latencyMs: 41 },
    { id: 'l-studio-registry', from: 'm-studio', to: 'ext-registry', kind: 'https', label: 'HTTPS, image pull', traffic: 0, latencyMs: 44 },
    { id: 'l-gate-registry', from: 'c-gate', to: 'ext-registry', kind: 'https', label: 'HTTPS, image pull', traffic: 2e3, latencyMs: 52 },
    // Backup relations, drawn only by the Backup layer.
    { id: 'b-git', from: 'c-api', to: 'ext-github', kind: 'copy', label: 'Git mirror of the committed workspace', layer: 'backup', traffic: 0 },
    { id: 'b-offhost', from: 'm-studio', to: 'tgt-offhost', kind: 'backup', label: 'Backup job: not configured', layer: 'backup', state: 'missing', traffic: 0 },
  ];

  // Per-node observations: health, release, drift, performance.
  const obs = {
    'm-studio': { health: 'ok', cpu: 46, load: 2.1, cores: 4, mem: [11.2, 16], release: 'r413', expected: 'r413' },
    'c-edge': { health: 'ok', cpu: 3, mem: [0.1, 16], release: 'r413', expected: 'r413' },
    'c-web': { health: 'ok', cpu: 2, mem: [0.2, 16], release: 'r412', expected: 'r413', behind: 1 },
    'c-api': { health: 'ok', cpu: 31, mem: [3.8, 16], release: 'r413', expected: 'r413' },
    'c-engine': { health: 'ok', cpu: 6, mem: [0.6, 16], release: 'r413', expected: 'r413' },
    'c-runner-a': { health: 'ok', cpu: 64, mem: [3.1, 16], release: 'r413', expected: 'r413', slots: [2, 2] },
    'c-runner-b': { health: 'ok', cpu: 22, mem: [1.9, 16], release: 'r413', expected: 'r413', slots: [1, 2] },
    'm-lab': { health: 'ok', cpu: 18, load: 1.3, cores: 12, mem: [26, 64] },
    'vm-lab': { health: 'ok', cpu: 35, load: 2.8, cores: 8, mem: [18.5, 32], release: 'r413', expected: 'r413' },
    'c-review-1': { health: 'ok', cpu: 41, mem: [2.4, 32], release: 'r413', expected: 'r413', slots: [1, 1] },
    'c-review-2': { health: 'ok', cpu: 12, mem: [1.1, 32], release: 'r413', expected: 'r413', slots: [0, 1] },
    'c-review-3': { health: 'warn', cpu: 2, mem: [0.4, 32], release: 'r413', expected: 'r413', slots: [0, 1], cli: { name: 'dotnet SDK 10', state: 'missing' } },
    'c-gate': { health: 'ok', cpu: 52, mem: [6.2, 32], release: 'r412', expected: 'r413', behind: 1, pendingInstall: true, slots: [0, 2] },
    'c-forwarder': { health: 'ok', cpu: 1, mem: [0.05, 32], release: 'r413', expected: 'r413', difference: 'undeclared' },
    'ext-cdn': { health: 'ok' }, 'ext-github': { health: 'ok' }, 'ext-registry': { health: 'ok' }, 'ext-models': { health: 'ok' },
    'tgt-offhost': { health: 'unknown', difference: 'declared-only' },
  };

  // Volumes per machine. The root disk fill level and RAID 1 are real; capacities are illustrative.
  const volumes = {
    'm-studio': [{ id: 'v-root', label: 'Root, software RAID 1', sizeGb: 120, usedPct: 84, growthGbPerDay: 0.9, real: ['usedPct', 'raid'],
      mounts: [
        { path: 'workspace', gb: 31, node: 'c-api' }, { path: 'project checkouts', gb: 3, node: 'c-runner-a' },
        { path: 'application state', gb: 3, node: 'c-api' }, { path: 'images and layers', gb: 38, node: 'm-studio' },
      ] }],
    'm-lab': [{ id: 'v-pool', label: 'Storage pool', sizeGb: 2000, usedPct: 38, growthGbPerDay: 0.4, mounts: [{ path: 'VM disk', gb: 400, node: 'vm-lab' }] }],
    'vm-lab': [{ id: 'v-vm', label: 'VM disk', sizeGb: 400, usedPct: 41, growthGbPerDay: 0.3,
      mounts: [{ path: 'gate worktrees', gb: 96, node: 'c-gate' }, { path: 'review worktrees', gb: 54, node: 'c-review-1' }, { path: 'object cache', gb: 12, node: 'c-gate' }] }],
  };

  // Stateful data locations with backup coverage. Classes are the facts' hints
  // from the code (see the Dossier inventory); the persona may weigh them differently.
  const dataLocations = [
    { id: 'dl-workspace-git', node: 'c-api', label: 'Workspace repository, committed part', detail: 'Task folders, receipts, reviews, prompts', sizeGb: 31, cls: 'must', real: true,
      copies: [{ kind: 'git-remote', target: 'ext-github', at: '2026-11-17T21:38:00Z', offHost: true, verified: 'push acknowledged' }], source: 'workspace-repository-lifecycle.md' },
    { id: 'dl-workspace-local', node: 'c-api', label: 'Workspace local state, not in git', detail: '.security (users, IAM, sessions, API tokens, sealed secrets) and attempt authority', sizeGb: null, cls: 'must', real: true,
      copies: [], source: 'SecurityStorePathPolicy.cs, workspace-repository-lifecycle.md' },
    { id: 'dl-appstate', node: 'c-api', label: 'Application state directory', detail: 'Batch gate store and leases, gate ledger, attempt records, ledgers, settings', sizeGb: 3, cls: 'must', real: true,
      copies: [{ kind: 'local-snapshot', target: 'm-studio', at: '2026-11-09T00:00:00Z', offHost: false, sameDisk: true, verified: 'never restored' }], source: 'studio-backup-restore.md' },
    { id: 'dl-secrets-key', node: 'm-studio', label: 'Secret store key', detail: 'Never in any backup by design; needs an escrowed copy', sizeGb: 0, cls: 'escrow', real: true,
      copies: [], source: 'KeyEncryptionKeySource.cs, studio-backup-restore.md' },
    { id: 'dl-backup-key', node: 'm-studio', label: 'Backup key', detail: 'Not created: the backup feature reports not-configured', sizeGb: 0, cls: 'escrow', real: true, missing: true,
      copies: [], source: 'BackupCipher.cs, /api/operator/backups' },
    { id: 'dl-cli-logins', node: 'c-runner-a', label: 'CLI logins and SSH keys', detail: 'Data home of the runner daemons', sizeGb: 0.1, cls: 'must',
      copies: [], source: 'BackupCredentialInventory.cs' },
    { id: 'dl-checkouts', node: 'c-runner-a', label: 'Project checkouts', detail: 'Re-cloned from origin; local refs need a git bundle', sizeGb: 3, cls: 'reproducible', real: true,
      copies: [{ kind: 'origin', target: 'ext-github', offHost: true }], source: 'BackupCheckouts.cs' },
    { id: 'dl-certs', node: 'c-edge', label: 'Edge certificates and ACME account', detail: 'Re-issued by ACME; archived to avoid rate limits', sizeGb: 0, cls: 'reproducible', real: true, copies: [], source: 'studio-backup-restore.md' },
    { id: 'dl-images', node: 'm-studio', label: 'Container images', detail: 'Pulled again from the registry', sizeGb: 38, cls: 'cache', copies: [{ kind: 'registry', target: 'ext-registry', offHost: true }] },
    { id: 'dl-caches', node: 'c-runner-a', label: 'Package and preparation caches', detail: 'node_modules, NuGet, preparation cache, gate source mirror', sizeGb: 9, cls: 'cache', copies: [] },
    { id: 'dl-lab-worktrees', node: 'c-gate', label: 'Gate and review worktrees, object cache', detail: 'Recreated from bundled branches and remotes', sizeGb: 162, cls: 'cache', copies: [] },
    { id: 'dl-lab-enrol', node: 'c-forwarder', label: 'Host enrolment credentials', detail: 'Re-issued by re-enrolment', sizeGb: 0, cls: 'reproducible', copies: [] },
  ];

  // The backup overview as /api/operator/backups reports it today (real state).
  const backupOverview = {
    state: 'not-configured', configured: false, running: false, keyId: null, secretKeyId: null,
    intervalHours: 6, verifyIntervalHours: 24, staleAfterHours: 12, retentionCount: 12,
    lastGood: null, lastVerification: null,
    offHost: { kind: 'none', target: null, state: 'not-configured' },
    warnings: [
      { code: 'backup-not-configured', severity: 'danger', message: 'No backup key file is configured.' },
    ],
    backups: [], generatedAt: AS_OF,
  };

  // Open problems pinned to nodes. Facts come from the APIs; the persona groups and ranks them.
  const warnings = [
    { id: 'w-backup-workspace-local', node: 'c-api', kind: 'backup', severity: 'bad', title: 'No copy of the workspace local state',
      what: '.security and attempt authority are excluded from git and from every backup.', blocks: 'Recovery of users, tokens, sealed secrets and attempt history after a disk loss.',
      next: 'Configure the Studio backup with an off-host target.', source: '/api/operator/backups, data locations', real: true },
    { id: 'w-backup-appstate', node: 'c-api', kind: 'backup', severity: 'bad', title: 'Application state: local snapshot only',
      what: 'The only copy is a manual snapshot from 9 November on the same disk.', blocks: 'Gate, batch and ledger recovery; recovery point 8 days.',
      next: 'Same backup job; verify one restore.', source: 'data locations', real: true },
    { id: 'w-secrets-key', node: 'm-studio', kind: 'backup', severity: 'bad', title: 'Secret store key: no escrow recorded',
      what: 'The key is never inside a backup by design.', blocks: 'Opening sealed secrets after a rebuild.',
      next: 'Owner escrows a copy and records the attestation.', source: 'key fingerprint, escrow attestations', real: true },
    { id: 'w-disk', node: 'm-studio', kind: 'disk', severity: 'warn', title: 'Root disk 84 % used',
      what: 'About 19 GB free; growing about 0.9 GB a day.', blocks: 'Gate and coding runs once free space passes the warning floor.',
      next: 'Prune images and old worktrees; plan a larger volume.', source: 'host telemetry, filesystems', real: true },
    { id: 'w-toolchain', node: 'c-review-3', kind: 'toolchain', severity: 'warn', title: 'Toolchain missing: dotnet SDK 10',
      what: 'The container image lacks a dependency that two cards need.', blocks: '2 cards refused dispatch (remote-dispatch-rejected).',
      next: 'Roll the review host image with the SDK.', source: 'installed CLIs, dispatch rejections' },
    { id: 'w-release', node: 'c-gate', kind: 'release', severity: 'warn', title: 'Release behind stable, install pending',
      what: 'Gate host runs r412; stable is r413.', blocks: 'Batch gates that need the new gate step.',
      next: 'Finish the pending host install.', source: 'host releases' },
    { id: 'w-parked', node: 'c-gate', kind: 'parked', severity: 'warn', title: '2 cards parked on gate infrastructure',
      what: 'gate-environment-exhausted on the last attempt.', blocks: '2 cards in the integration lane.',
      next: 'Retry after the host install.', source: 'runner infrastructure failures' },
    { id: 'w-cred', node: 'c-runner-b', kind: 'credential', severity: 'warn', title: 'CLI login expires in 3 days',
      what: 'credentials-expiring on runner daemon B.', blocks: 'Coding runs on this daemon after expiry.',
      next: 'Renew the login.', source: 'capability health' },
    { id: 'w-drift', node: 'c-forwarder', kind: 'drift', severity: 'info', title: 'Undeclared container',
      what: 'The link forwarder is observed but not in the placement file.', blocks: 'Nothing; the view cannot check its declared state.',
      next: 'Declare it in the placement file.', source: 'structure observation' },
  ];

  // Inside a service: slots, running cards, gate runs (level 4 content).
  const inside = {
    'c-runner-a': { slots: [2, 2], items: [{ kind: 'coding', text: 'card 101 coding, 12 min' }, { kind: 'coding', text: 'card 98 fix round, 3 min' }] },
    'c-runner-b': { slots: [1, 2], items: [{ kind: 'coding', text: 'card 99 coding, 25 min' }] },
    'c-gate': { slots: [0, 2], items: [] },
    'c-review-1': { slots: [1, 1], items: [{ kind: 'review', text: 'card 97 review, 6 min' }] },
    'c-review-2': { slots: [0, 1], items: [] },
    'c-review-3': { slots: [0, 1], items: [] },
    'c-api': { items: [{ kind: 'info', text: 'task authority, 42 open cards' }] },
    'c-engine': { items: [{ kind: 'info', text: '3 decisions a minute' }] },
    'c-edge': { items: [{ kind: 'info', text: '2 routes, certificate 61 days' }] },
    'c-web': { items: [{ kind: 'info', text: 'static app, 3 sessions' }] },
    'c-forwarder': { items: [{ kind: 'info', text: 'tunnel endpoint, 5 peers' }] },
  };

  // Recorded deltas for the replay: seconds from start, in the delta envelope of AGT-3954.
  // Each event carries one or more operations; the client batches them to one visual update a second.
  const events = [
    { t: 1.2, ops: [{ op: 'patch', node: 'c-runner-a', set: { cpu: 71 } }, { op: 'patch', node: 'c-api', set: { cpu: 34 } }] },
    { t: 2.1, ops: [{ op: 'patch', node: 'c-review-1', set: { cpu: 44 } }] },
    { t: 3.0, ops: [{ op: 'work', card: 'card 101', kind: 'coding', step: 'coding finished', from: 'c-runner-a', path: ['c-runner-a', 'c-api', 'c-forwarder', 'c-gate'] },
      { op: 'inside', node: 'c-runner-a', remove: 'card 101 coding, 12 min' }, { op: 'patch', node: 'c-runner-a', set: { slots: [1, 2] } }] },
    { t: 6.4, ops: [{ op: 'inside', node: 'c-gate', add: { kind: 'gate', text: 'batch 7: card 101, full suite' } }, { op: 'patch', node: 'c-gate', set: { slots: [1, 2], cpu: 78 } }, { op: 'effect', kind: 'spin', node: 'c-gate', id: 'gate-7', color: 'gate' }] },
    { t: 8.0, ops: [{ op: 'deploy', node: 'c-web', step: 'deploy r413: pull image', state: 'running' }, { op: 'effect', kind: 'spin', node: 'c-web', id: 'deploy-web', color: 'link' }] },
    { t: 8.6, ops: [{ op: 'patch', node: 'c-runner-b', set: { cpu: 27 } }, { op: 'patch', node: 'm-studio', set: { cpu: 52 } }] },
    { t: 11.5, ops: [{ op: 'deploy', node: 'c-web', step: 'deploy r413: restart container', state: 'running' }, { op: 'restart', node: 'c-web' }, { op: 'patch', node: 'c-web', set: { health: 'warn' } }] },
    { t: 13.2, ops: [{ op: 'patch', node: 'c-web', set: { health: 'ok', release: 'r413', behind: 0 } }, { op: 'deploy', node: 'c-web', step: 'deploy r413: healthy', state: 'done' }, { op: 'effect-end', id: 'deploy-web' }] },
    { t: 15.0, ops: [{ op: 'storage', machine: 'm-studio', volume: 'v-root', set: { usedPct: 84.3 } }] },
    { t: 17.5, ops: [{ op: 'inside', node: 'c-gate', remove: 'batch 7: card 101, full suite' }, { op: 'effect-end', id: 'gate-7' }, { op: 'patch', node: 'c-gate', set: { slots: [0, 2], cpu: 30 } },
      { op: 'work', card: 'card 101', kind: 'gate', step: 'batch gate passed', from: 'c-gate', path: ['c-gate', 'c-forwarder', 'c-review-2'] }] },
    { t: 20.5, ops: [{ op: 'inside', node: 'c-review-2', add: { kind: 'review', text: 'card 101 review' } }, { op: 'patch', node: 'c-review-2', set: { slots: [1, 1], cpu: 38 } }] },
    { t: 21.0, ops: [{ op: 'patch', node: 'c-review-1', set: { cpu: 18 } }, { op: 'patch', node: 'c-runner-a', set: { cpu: 48 } }] },
    { t: 24.0, ops: [{ op: 'link', link: 'l-tunnel', set: { state: 'down' } }, { op: 'patch', node: 'c-gate', set: { health: 'unknown' } },
      { op: 'patch', node: 'c-review-1', set: { health: 'unknown' } }, { op: 'patch', node: 'c-review-2', set: { health: 'unknown' } }, { op: 'patch', node: 'c-review-3', set: { health: 'unknown' } },
      { op: 'warn', add: { id: 'w-tunnel', node: 'c-forwarder', kind: 'link', severity: 'bad', title: 'Tunnel down', what: 'No heartbeat from the home lab since the last probe.', blocks: '1 review and every gate run wait.', next: 'Reconnect the link; check the forwarder.', source: 'link supervisor' } }] },
    { t: 26.0, ops: [{ op: 'assess', persona: 'reliability-engineer', spec: 'reliability-v2' }] },
    { t: 31.0, ops: [{ op: 'link', link: 'l-tunnel', set: { state: 'up' } }, { op: 'patch', node: 'c-gate', set: { health: 'ok' } },
      { op: 'patch', node: 'c-review-1', set: { health: 'ok' } }, { op: 'patch', node: 'c-review-2', set: { health: 'ok' } }, { op: 'patch', node: 'c-review-3', set: { health: 'warn' } },
      { op: 'warn', remove: 'w-tunnel' }, { op: 'assess', persona: 'reliability-engineer', spec: 'reliability' }] },
    { t: 33.0, ops: [{ op: 'inside', node: 'c-review-2', remove: 'card 101 review' }, { op: 'patch', node: 'c-review-2', set: { slots: [0, 1], cpu: 10 } },
      { op: 'work', card: 'card 101', kind: 'review', step: 'review passed', from: 'c-review-2', path: ['c-review-2', 'c-forwarder', 'c-api'] }] },
    { t: 35.0, ops: [{ op: 'inside', node: 'c-runner-a', add: { kind: 'coding', text: 'card 102 coding' } }, { op: 'patch', node: 'c-runner-a', set: { slots: [2, 2], cpu: 88 } }] },
    { t: 37.5, ops: [{ op: 'dispatch-rejected', node: 'c-review-3', card: 'card 103', reason: 'capability dotnet-10 missing' }, { op: 'warn', patch: 'w-toolchain', set: { blocks: '3 cards refused dispatch (remote-dispatch-rejected).' } }] },
    { t: 40.0, ops: [{ op: 'patch', node: 'c-runner-a', set: { cpu: 62 } }, { op: 'patch', node: 'm-studio', set: { cpu: 49 } }, { op: 'backup-check', state: 'not-configured' }] },
    { t: 44.0, ops: [{ op: 'end' }] },
  ];

  // Persona view specifications (structure-view-spec/v1). Produced by a model
  // call from the facts named in factsUsed; validated before rendering.
  const specs = {
    'operations-warden': {
      schemaVersion: 'structure-view-spec/v1', id: 'ows-2026-11-17-2130', revision: 3,
      persona: { id: 'operations-warden', role: 'operations', title: 'Operations warden' },
      createdAt: '2026-11-17T21:30:00Z', model: { capabilityClass: 'balanced', id: 'claude-sonnet-5-5' }, confidence: 0.86,
      tokens: { input: 9400, output: 1100 },
      factsUsed: ['backups.overview@2026-11-17T21:30Z', 'structure.dataLocations@seq1838', 'structure.volumes@seq1838', 'capabilities.credentials@2026-11-17T21:25Z'],
      headline: 'One of six locations that must survive a disk loss has an off-host copy.',
      judgement: 'Only the committed part of the workspace leaves the server, through its git remote. The workspace local state, the application state, the CLI logins and both keys have no off-host copy; the application state\'s only copy is an 8-day-old snapshot on the same RAID pair. RAID 1 survives one failed disk, not deletion, corruption or the loss of the server.',
      layers: { enable: ['backup', 'warnings'], emphasise: 'backup' },
      emphasis: [{ node: 'c-api', weight: 'strong' }, { node: 'm-studio', weight: 'strong' }, { node: 'tgt-offhost', weight: 'normal' }],
      annotations: [
        { id: 'a1', node: 'c-api', tone: 'bad', title: 'No backup here', text: 'Workspace local state and application state have no off-host copy.', factRefs: ['dl-workspace-local', 'dl-appstate'] },
        { id: 'a2', node: 'm-studio', tone: 'bad', title: 'Secret store key not escrowed', text: 'Without a copy, sealed secrets cannot be opened after a rebuild.', factRefs: ['dl-secrets-key'] },
        { id: 'a3', node: 'tgt-offhost', tone: 'warn', title: 'No off-host target', text: 'Choose one; the backup job needs it and a backup key.', factRefs: ['backups.offHost'] },
        { id: 'a4', node: 'm-studio', tone: 'warn', title: 'Disk fills in about 21 days', text: '84 % used, about 0.9 GB a day.', factRefs: ['v-root'] },
      ],
      ranking: ['w-backup-workspace-local', 'w-secrets-key', 'w-backup-appstate', 'w-disk', 'w-cred', 'w-toolchain', 'w-release', 'w-parked', 'w-drift'],
      goals: [{ id: 'backup-coverage', title: 'Every must-keep location has a verified off-host copy', done: 1, total: 6,
        steps: [
          { kind: 'owner-decision', title: 'Choose the off-host target', detail: 'Decision D9 of this Dossier; SFTP to a second site is recommended.', state: 'open' },
          { kind: 'owner-decision', title: 'Decide key custody', detail: 'Backup key and secret store key escrowed apart, as two password manager entries.', state: 'open' },
          { kind: 'card', title: 'Configure the Studio backup and verify one restore', detail: 'Backup:KeyFile, Backup:Target, sources workspace, home, stack; first verified archive.', state: 'proposed' },
          { kind: 'card', title: 'Record escrow attestations', detail: 'Owner attests both key copies by fingerprint; the view shows the date.', state: 'proposed' },
        ] }],
    },
    'reliability-engineer': {
      schemaVersion: 'structure-view-spec/v1', id: 'rel-2026-11-17-2140', revision: 7,
      persona: { id: 'reliability-engineer', role: 'reliability', title: 'Reliability engineer' },
      createdAt: '2026-11-17T21:40:00Z', model: { capabilityClass: 'balanced', id: 'claude-sonnet-5-5' }, confidence: 0.78,
      tokens: { input: 8200, output: 900 },
      factsUsed: ['structure@seq1840', 'links@seq1840', 'telemetry.hosts@2026-11-17T21:39Z', 'dispatch.rejections@2026-11-17T21:35Z', 'hostReleases@2026-11-17T21:30Z'],
      headline: 'The tunnel is the only path to every gate and review run.',
      judgement: 'All gate and review capacity sits behind one tunnel and one forwarder in the home lab. Review host 3 refuses work for a missing SDK, so two review slots carry the load. The gate host is one release behind and two cards wait on it. Coding capacity on the cloud server is fine.',
      layers: { enable: ['perf', 'warnings'], emphasise: 'perf' },
      emphasis: [{ node: 'c-forwarder', weight: 'strong' }, { node: 'c-review-3', weight: 'normal' }, { node: 'c-gate', weight: 'normal' }],
      annotations: [
        { id: 'r1', link: 'l-tunnel', tone: 'warn', title: 'Single path', text: 'Every gate and review run crosses this tunnel.', factRefs: ['l-tunnel'] },
        { id: 'r2', node: 'c-review-3', tone: 'warn', title: 'Refuses work', text: 'dotnet SDK 10 missing; 2 cards refused.', factRefs: ['w-toolchain'] },
        { id: 'r3', node: 'c-gate', tone: 'warn', title: 'One release behind', text: 'Pending install; 2 cards parked.', factRefs: ['w-release', 'w-parked'] },
      ],
      ranking: ['w-toolchain', 'w-release', 'w-parked', 'w-disk', 'w-cred', 'w-backup-workspace-local', 'w-backup-appstate', 'w-secrets-key', 'w-drift'],
      goals: [{ id: 'review-capacity', title: 'All three review hosts accept every card kind', done: 2, total: 3,
        steps: [{ kind: 'card', title: 'Roll the review host image with dotnet SDK 10', detail: 'Then clear the two dispatch rejections.', state: 'proposed' }] }],
    },
    'reliability-v2': {
      schemaVersion: 'structure-view-spec/v1', id: 'rel-2026-11-17-2146', revision: 8,
      persona: { id: 'reliability-engineer', role: 'reliability', title: 'Reliability engineer' },
      createdAt: '2026-11-17T21:46:00Z', model: { capabilityClass: 'balanced', id: 'claude-sonnet-5-5' }, confidence: 0.9,
      tokens: { input: 8600, output: 700 }, trigger: 'link.down l-tunnel',
      factsUsed: ['links@seq1851', 'structure@seq1851', 'attention@2026-11-17T21:46Z'],
      headline: 'Tunnel down: gate and review work waits.',
      judgement: 'The link supervisor lost the tunnel heartbeat. One review and the gate queue wait; coding continues on the cloud server. Reconnect the link before anything else.',
      layers: { enable: ['perf', 'warnings', 'health'], emphasise: 'perf' },
      emphasis: [{ node: 'c-forwarder', weight: 'strong' }],
      annotations: [{ id: 'r9', link: 'l-tunnel', tone: 'bad', title: 'Tunnel down', text: 'Reconnect the link; 1 review and the gate queue wait.', factRefs: ['l-tunnel', 'w-tunnel'] }],
      ranking: ['w-tunnel', 'w-toolchain', 'w-release', 'w-parked', 'w-disk', 'w-cred', 'w-backup-workspace-local', 'w-backup-appstate', 'w-secrets-key', 'w-drift'],
      goals: [{ id: 'review-capacity', title: 'All three review hosts accept every card kind', done: 2, total: 3,
        steps: [{ kind: 'operation', title: 'Reconnect the tunnel', detail: 'Links › reconnect; then check the forwarder.', state: 'proposed' }] }],
    },
    // A deliberately invalid specification: markup in a text field, an unknown
    // key and a node that does not exist. The renderer falls back to facts.
    invalid: {
      schemaVersion: 'structure-view-spec/v1', id: 'bad-1', revision: 1,
      persona: { id: 'operations-warden', role: 'operations', title: 'Operations warden' },
      createdAt: '2026-11-17T21:35:00Z', model: { capabilityClass: 'balanced', id: 'claude-sonnet-5-5' }, confidence: 0.7,
      factsUsed: ['backups.overview@2026-11-17T21:30Z'],
      headline: 'Backups <b>missing</b>', judgement: 'x', style: 'color: red',
      annotations: [{ id: 'z', node: 'c-database', tone: 'bad', title: 'No backup', text: 'x' }],
    },
  };

  const personas = [
    { id: 'facts', title: 'Facts only', role: 'none' },
    { id: 'operations-warden', title: 'Operations warden', role: 'operations', spec: 'operations-warden' },
    { id: 'reliability-engineer', title: 'Reliability engineer', role: 'reliability', spec: 'reliability-engineer' },
  ];

  const api = { AS_OF, GB, nodes, links, obs, volumes, dataLocations, backupOverview, warnings, inside, events, specs, personas };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LSCModel = api;
})(typeof window !== 'undefined' ? window : globalThis);
