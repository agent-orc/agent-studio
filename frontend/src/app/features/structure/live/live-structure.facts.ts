import { LIVE_METRICS } from './live-structure.layout';
import { COVERAGE_WORD, NOT_OBSERVED, ago, diskPressure, diskUsed, gib, percent, rate, runsHere, size, subtreeWarnings,
  type LiveLayer, type LiveNode, type LiveScene } from './live-structure.model';

/**
 * Facts of a node, each tagged with the zoom level that adds it and the layer it
 * belongs to (AGT-4024, semantic zoom). The canvas shows the facts up to the
 * level a node has on screen; the card details show all of them. A fact nobody
 * reports is kept as a "not observed" fact with the reason it is missing. It
 * never appears before level 3, so the far levels stay calm; from the container
 * level on the canvas names what is not observed and why (`canvasGaps`), and at
 * the machine level a node that has no fact at all says so too.
 */
export type LiveLevel = 1 | 2 | 3 | 4;
/** Level 5 is the card details only. */
export type LiveFactLevel = 2 | 3 | 4 | 5;
export const LIVE_LEVELS: readonly { level: LiveLevel; name: string; adds: string }[] = [
  { level: 1, name: 'Overview', adds: 'Name, role and health mark' },
  { level: 2, name: 'Machine', adds: 'State, load, memory, disk, slots and the count of what runs there' },
  { level: 3, name: 'Container', adds: 'Image and version, restarts, uptime, current job; what is not observed, and why' },
  { level: 4, name: 'Closest', adds: 'Recent events and every value of the active layers' },
];
export interface LiveFact {
  key: string; level: LiveFactLevel; layer: LiveLayer | null;
  label: string;
  /** Wordings from long to short. The canvas takes the first that fits and leaves the fact out when none does. */
  values: string[];
  tone?: 'success' | 'warning' | 'danger' | 'muted';
  /** 0 to 1: drawn as a meter between label and value. */
  meter?: number;
  cells?: { busy: number; total: number; held?: number | null };
  /** Drawn as a tinted band across the node (a warning in words). */
  band?: boolean;
  observed: boolean;
  /** Not observed only: why the Studio has no value, as one sentence for the card details. */
  why?: string;
  /** Not observed only: the reason the canvas groups it under. */
  gap?: LiveGapKind;
}
/** Why a value is missing: nothing reports from the node, nobody collects it, the read was refused, or it is listed elsewhere only. */
export type LiveGapKind = 'source' | 'collect' | 'rights' | 'scope';
/** Missing facts of one reason, as the canvas says them. Wordings from long to short; none is cut. */
export interface LiveGap { kind: LiveGapKind; labels: string[]; values: string[]; }

const isFrame = (node: LiveNode): boolean => node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group' || node.kind === 'boundary';
const isUnit = (node: LiveNode): boolean => (node.kind === 'container' || node.kind === 'service') && !(node.parent && (node.parent.kind === 'container' || node.parent.kind === 'service'));
const storageOf = (node: LiveNode): LiveNode | null => node.kind === 'disk' || node.disks.length || node.locations.length ? node
  : node.children.find(child => child.kind === 'disk') ?? null;
const isDigest = (value: string): boolean => /^sha256:[0-9a-f]{12,}$/i.test(value) || /^[0-9a-f]{40,}$/i.test(value);
const shortDigest = (value: string): string => value.replace(/^sha256:/i, '').slice(0, 12);
const clock = (iso: string): string => new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }).format(new Date(iso));

const cache = new WeakMap<LiveNode, LiveFact[]>();

/** Every fact of a node, in a fixed order. Cached for the lifetime of the scene. */
export function nodeFacts(node: LiveNode, scene: LiveScene): LiveFact[] {
  const cached = cache.get(node);
  if (cached) return cached;
  const facts: LiveFact[] = [];
  const now = scene.generatedAt;
  const add = (level: LiveFactLevel, layer: LiveLayer | null, key: string, label: string, values: (string | null | undefined)[],
    extra: Partial<LiveFact> = {}): void => {
    const list = values.filter((value): value is string => !!value);
    if (list.length) facts.push({ key, level, layer, label, values: list, observed: true, ...extra });
  };
  const reporting = !!node.source?.facts?.hostId;
  const source = sourceGap(node);
  /** `why` is a sentence of its own, or one of the reasons: the node has no reporter, or nobody collects the value. */
  const missing = (level: LiveFactLevel, layer: LiveLayer | null, key: string, label: string, why: string | LiveGapKind = 'source'): void => {
    const gap: LiveGapKind = why === 'source' || why === 'collect' || why === 'rights' || why === 'scope' ? why : 'collect';
    // A host that reports, and still lacks a value, lacks it because its report does not carry it.
    const sentence = why !== gap ? why : gap === 'source' ? (reporting ? 'The report of this host does not carry this value.' : source.long) : GAP_WHY[gap].long;
    facts.push({ key, level: Math.max(level, 3) as LiveFactLevel, layer, label, values: [NOT_OBSERVED], tone: 'muted', observed: false,
      why: sentence, gap: gap === 'source' && reporting ? 'collect' : gap });
  };
  const runs = isUnit(node) || node.kind === 'machine' || node.kind === 'vm';
  const perf = node.perf;

  if (node.kind === 'boundary') { cache.set(node, installationFacts(scene)); return cache.get(node)!; }

  // Warnings first in the list: the first one is the band a node carries from level 2 on.
  node.warnings.forEach((warning, index) => add(index === 0 ? 2 : 3, 'warnings', `warning:${warning.id}`, 'warning',
    [warning.title, warning.short], { tone: warning.tone, band: index === 0 }));
  node.warnings.forEach(warning => add(5, 'warnings', `detail:${warning.id}`, warning.title, [warning.detail], { tone: warning.tone }));
  if (isFrame(node)) {
    const inside = subtreeWarnings(node).length - node.warnings.length;
    if (inside) add(2, 'warnings', 'inside', 'warnings', [`${inside} inside`, String(inside)], { tone: 'warning' });
  }

  const readings = node.source?.facts;
  const stamp = readings?.sample;
  if (stamp && now && Date.parse(stamp.freshUntil) < Date.parse(now))
    add(2, null, 'freshness', 'stale sample', [`${ago(stamp.observedAt, now)} ago`], { tone: 'warning' });
  const capacity = readings?.capacity;
  if (capacity?.queueLength != null) add(2, 'perf', 'queue', 'queued', [String(capacity.queueLength)]);
  else if (capacity) missing(3, 'perf', 'queue', 'queued', 'A runner queue sample is needed.');
  const api = readings?.api;
  if (api?.requestsPerSecond != null) add(2, 'perf', 'requests', 'traffic', [`${api.requestsPerSecond.toFixed(1)} req/s`]);
  else if (api || node.source?.role === 'web') missing(3, 'perf', 'requests', 'requests/s', 'Requires a request counter sampled over time.');
  if (api?.sample && now && Date.parse(api.sample.freshUntil) < Date.parse(now))
    add(2, 'perf', 'api-age', 'stale API sample', [ago(api.sample.observedAt, now) + ' ago'], { tone: 'warning' });
  const resource = readings?.resources ?? readings?.container?.resources;
  if (resource?.cpuCoresUsed != null) add(2, 'perf', 'core-use', 'CPU use', [`${resource.cpuCoresUsed.toFixed(2)} cores`]);
  if (resource?.cpuUsageMicroseconds != null) add(3, 'perf', 'cpu-time', 'CPU time', [`${(resource.cpuUsageMicroseconds / 1e6).toFixed(1)} s`]);
  for (const [index, worker] of (readings?.workers ?? []).entries()) {
    const usage = worker.resources;
    add(4, 'perf', `worker:${index}`, worker.cardKey ?? 'Worker', [usage?.memoryBytes != null ? size(usage.memoryBytes) : usage?.cpuUsageMicroseconds != null ? `${(usage.cpuUsageMicroseconds / 1e6).toFixed(1)} CPU s` : 'worker reported']);
  }
  if (node.kind === 'external' && ['codex', 'claude'].includes(node.source?.role ?? '') && !readings?.weeklyWindows?.length)
    missing(3, 'perf', 'weekly-window', 'weekly window', 'Requires a subscription usage sample with its weekly reset.');
  const device = readings?.device;
  if (device?.sizeBytes != null) add(2, 'storage', 'device', device.raidLevel ?? 'device size', [gib(device.sizeBytes)]);
  if (device?.members.length) add(3, 'storage', 'members', 'members', [device.members.join(', ')]);
  for (const [index, window] of (readings?.weeklyWindows ?? []).entries()) {
    if (window.usedPercent != null) add(2, 'perf', `window:${index}`, window.label, [`${window.usedPercent}% used`], { meter: window.usedPercent / 100 });
    if (now && Date.parse(window.sample.freshUntil) < Date.parse(now))
      add(2, 'perf', 'stale-window:' + index, window.label + ' stale', [ago(window.sample.observedAt, now) + ' ago'], { tone: 'warning' });
    if (window.resetAt) add(3, 'perf', `reset:${index}`, 'resets', [clock(window.resetAt)]);
  }
  // Performance: meters and slot cells.
  if (runs) {
    if (perf?.cpu != null) add(2, 'perf', 'cpu', 'cpu', [perf.cores ? `${percent(perf.cpu)} of ${perf.cores} cores` : null, percent(perf.cpu)], { meter: perf.cpu });
    else missing(2, 'perf', 'cpu', 'cpu');
    if (perf?.memUsed != null) add(2, 'perf', 'memory', 'memory', [perf.memTotal ? `${gib(perf.memUsed)} of ${gib(perf.memTotal)}` : null, size(perf.memUsed)],
      perf.memTotal ? { meter: perf.memUsed / perf.memTotal } : {});
    else if (perf?.memTotal != null) add(2, 'perf', 'memory', 'installed memory', [gib(perf.memTotal)]);
    else missing(2, 'perf', 'memory', 'memory');
  }
  if (node.slots) {
    const { busy, total } = node.slots;
    add(2, 'perf', 'slots', 'slots', total != null ? [`${busy} of ${total} in use`, `${busy}/${total}`] : [`${busy} in use`, String(busy)],
      total != null && total <= 64 ? { cells: { busy, total, held: node.source?.facts?.capacity?.heldSlots } } : {});
  } else if (reporting) missing(2, 'perf', 'slots', 'slots', 'collect');
  if (perf?.load) add(3, 'perf', 'load', 'load', [perf.load.map(value => value.toFixed(2)).join(' · '), perf.load[0].toFixed(2)]);
  else if (reporting || node.kind === 'machine') missing(3, 'perf', 'load', 'load');
  if (node.source?.observation?.activeGates != null) add(3, 'perf', 'gates', 'active gates', [String(node.source.observation.activeGates)]);
  if (perf?.observedAt) add(4, 'perf', 'sampled', 'sampled', [`${ago(perf.observedAt, now)} ago`]);

  // Network: one row per route, then traffic.
  const links = node.links.filter(link => link.layer === 'network');
  if (links.length) {
    const up = links.filter(link => link.health.tone === 'success').length, down = links.filter(link => link.health.tone === 'danger').length;
    add(2, 'network', 'links', 'links', [down ? `${down} of ${links.length} down` : up === links.length ? `${links.length} up` : `${up} of ${links.length} observed up`, String(links.length)],
      down ? { tone: 'danger' } : {});
    for (const link of links) {
      const peer = link.a === node ? link.b : link.a;
      const state = link.health.word.toLowerCase();
      add(3, 'network', `link:${link.id}`, peer.short, [
        [state, link.latencyMs != null ? `${link.latencyMs.toFixed(1)} ms` : null, link.transport].filter(Boolean).join(' · '),
        [state, link.latencyMs != null ? `${link.latencyMs.toFixed(1)} ms` : null].filter(Boolean).join(' · '), state],
        link.health.tone === 'danger' ? { tone: 'danger' } : link.health.tone === 'neutral' ? { tone: 'muted' } : {});
    }
  } else if (runs) add(4, 'network', 'links', 'links', ['none reported'], { tone: 'muted' });
  if (perf?.rx != null && perf.tx != null) add(4, 'network', 'traffic', 'traffic', [`in ${rate(perf.rx)} · out ${rate(perf.tx)}`, `in ${rate(perf.rx)}`]);
  else if (runs) missing(4, 'network', 'traffic', 'traffic', reporting ? 'collect' : 'Traffic is measured for a reporting host as a whole, not for this node.');
  if (runs && node.kind !== 'machine' && node.kind !== 'vm') missing(3, null, 'ports', 'ports', 'No source lists published or listening ports.');

  // Storage: the fullest disk first, then each mount, then each data location.
  const storage = storageOf(node);
  if (storage?.disks.length) {
    const worst = [...storage.disks].sort((a, b) => diskUsed(b) - diskUsed(a))[0];
    const level = diskPressure(worst);
    add(2, 'storage', 'disk', 'disk', [`${percent(diskUsed(worst))} of ${gib(worst.totalBytes)}`, percent(diskUsed(worst))],
      { meter: diskUsed(worst), ...(level ? { tone: level } : {}) });
    for (const disk of storage.disks) add(3, 'storage', `mount:${disk.mount}`, disk.mount,
      [`${gib(disk.usedBytes)} of ${gib(disk.totalBytes)} · ${disk.purpose}`, `${gib(disk.freeBytes)} free`, percent(diskUsed(disk))]);
  } else if (runs) missing(2, 'storage', 'disk', 'disk');
  for (const location of storage?.locations ?? []) add(4, 'storage', `data:${location.id}`, location.label,
    [!location.exists ? 'not visible to the API' : location.sizeBytes != null ? `${location.sizeComplete ? '' : 'at least '}${size(location.sizeBytes)}` : 'size not measured yet'],
    location.exists ? {} : { tone: 'muted' });

  // Backup: coverage of what must be kept, then the last run and the off-host copy.
  if (storage?.locations.length) {
    const present = storage.locations.filter(location => location.exists);
    const covered = present.filter(location => location.coverage === 'backup' || location.coverage === 'both').length;
    add(2, 'backup', 'coverage', 'backed up', [`${covered} of ${present.length} data locations`, `${covered} of ${present.length}`],
      { tone: covered === present.length ? 'success' : 'warning' });
    const overview = scene.backup.overview;
    if (overview) {
      add(3, 'backup', 'state', 'backup', [scene.backup.health.word.replace(/^Backups /, '')], { tone: overview.state === 'ok' ? 'success' : overview.state === 'failing' ? 'danger' : 'warning' });
      add(3, 'backup', 'last', 'last backup', overview.lastGood
        ? [`${ago(overview.lastGood.createdAt, now)} ago · ${overview.lastGood.verification}`, `${ago(overview.lastGood.createdAt, now)} ago`] : ['none yet'],
        overview.lastGood ? {} : { tone: 'warning' });
      add(3, 'backup', 'offhost', 'off-host copy', [overview.offHost.state.replace('-', ' ')], overview.offHost.state === 'current' ? { tone: 'success' } : { tone: 'warning' });
      add(5, 'backup', 'next', 'next backup', [overview.nextBackupAt ? clock(overview.nextBackupAt) : overview.configured ? 'not scheduled' : null]);
      add(5, 'backup', 'policy', 'policy', [overview.configured ? `every ${overview.intervalHours} h · keeps ${overview.retentionCount}` : null]);
    } else { missing(3, 'backup', 'last', 'last backup', 'rights'); missing(3, 'backup', 'offhost', 'off-host copy', 'rights'); }
    for (const location of present) add(4, 'backup', `cover:${location.id}`, location.label, [COVERAGE_WORD[location.coverage] ?? location.coverage],
      { tone: location.coverage === 'none' ? 'warning' : location.coverage === 'git-remote' ? 'muted' : 'success' });
  } else if (node.kind === 'target') {
    const overview = scene.backup.overview;
    if (overview) {
      add(3, 'backup', 'uploaded', 'last upload', [overview.offHost.lastUploadedAt ? `${ago(overview.offHost.lastUploadedAt, now)} ago` : 'none']);
      add(5, 'backup', 'error', 'last error', [overview.offHost.lastError]);
    } else missing(2, 'backup', 'uploaded', 'last upload', 'rights');
  } else if (reporting) missing(3, 'backup', 'data', 'data locations', 'scope');

  // What runs here, and the container level: state, image and version, restarts, uptime, current job, last deploy.
  if (isFrame(node)) add(2, null, 'runs', 'runs', [runsHere(node).join(' · '), runsHere(node)[0]]);
  // Every node says how it is, or that nobody looks: from the container level on no node is a bare box.
  const seenAt = readings?.lastSeenAt ?? stamp?.observedAt ?? node.source?.observation?.observedAt ?? null;
  const tone = node.health.tone === 'success' || node.health.tone === 'warning' || node.health.tone === 'danger' ? { tone: node.health.tone } : {};
  if (node.kind === 'disk') {
    if (node.disks.length) add(3, null, 'status', 'reported', [`${node.disks.length} ${node.disks.length === 1 ? 'disk' : 'disks'} · ${node.locations.length} data locations`, `${node.disks.length} ${node.disks.length === 1 ? 'disk' : 'disks'}`]);
    else missing(3, null, 'status', 'disk use');
  } else if (node.kind === 'target') {
    if (scene.backup.overview) add(2, null, 'status', 'state', [node.health.word.toLowerCase()], tone);
    else missing(3, null, 'status', 'state', 'rights');
  } else if (node.health.word !== 'Not observed') add(2, null, 'status', 'state',
    [seenAt ? `${node.health.word.toLowerCase()} · seen ${ago(seenAt, now)} ago` : null, node.health.word.toLowerCase()], tone);
  else missing(3, null, 'status', 'state');
  if (runs && (node.kind !== 'vm' || node.runners.length > 1)) {
    const multiple = node.runners.length > 1;
    const reports = multiple ? node.runners.map(runner => {
      const source = scene.order.find(item => item.source?.unitId?.toLowerCase() === runner.runnerId.toLowerCase())?.source;
      const roles = source?.facts?.roles?.length ? source.facts.roles : source?.role ? [source.role] : [];
      return { runner, source, prefix: `runner:${runner.runnerId}:`, label: `${roles.length ? roles.join(' and ') : runner.name} (${runner.runnerId}) ` };
    }) : [{ runner: node.runner, source: node.source, prefix: '', label: '' }];
    if (multiple) add(3, null, 'runners', 'runners', [reports.map(report => report.label.trim()).join(' · ')]);
    for (const { runner, source, prefix, label } of reports) {
      const image = source?.facts?.container?.imageTag ?? runner?.release?.imageRef ?? (!multiple && node.version && isDigest(node.version) ? `sha256:${shortDigest(node.version)}` : null);
      if (image) add(3, null, `${prefix}image`, `${label}image`, [image, image.split('/').at(-1), image.split('@')[0].split('/').at(-1)]);
      else if (multiple || node.kind === 'container') missing(3, null, `${prefix}image`, `${label}image`, 'No image name or tag is reported for this runner or container.');
      const version = multiple ? runner?.release?.version ?? null
        : node.version && !isDigest(node.version) ? node.version : runner?.release?.version ?? null;
      const release = source?.facts?.release;
      if (version) add(3, null, `${prefix}version`, `${label}version`, [release?.stableVersion && release.stableVersion !== version
        ? `${version} · stable ${release.stableVersion}` : null, version], release?.behind ? { tone: 'warning' } : {});
      else if (multiple || node.kind !== 'machine') missing(3, null, `${prefix}version`, `${label}version`, reporting ? 'collect' : 'No version is reported for this node.');
      const restarts = source?.facts?.container?.restarts?.toString() ?? /restarts:\s*(\d+)/.exec(source?.observation?.detail ?? '')?.[1];
      if (runner?.reviewRestarts) add(3, null, `${prefix}restarts`, `${label}restarts`, [`${runner.reviewRestarts.length} of the review daemon`, String(runner.reviewRestarts.length)]);
      else if (restarts) add(3, null, `${prefix}restarts`, `${label}restarts`, [restarts]);
      else if (multiple || node.kind !== 'machine') missing(3, null, `${prefix}restarts`, `${label}restarts`, 'Restart counts are collected for the review daemon of a runner only.');
      const since = runner?.restartedAt ?? runner?.registeredAt ?? null;
      if (source?.facts?.container?.uptimeSeconds != null) add(3, null, `${prefix}uptime`, `${label}uptime`, [`${Math.round(source.facts.container.uptimeSeconds / 60)} min`]);
      else if (since) add(3, null, `${prefix}uptime`, `${label}uptime`, [`${ago(since, now)} · since ${clock(since)}`, ago(since, now)]);
      else if (multiple || node.kind !== 'machine') missing(3, null, `${prefix}uptime`, `${label}uptime`, 'Start times are known for runner processes only.');
    }
    const cards = node.slots?.cards ?? [];
    if (cards.length) add(3, null, 'job', 'current job', [cards.join(', '), cards.length > 1 ? `${cards[0]} +${cards.length - 1}` : null, `${cards.length} cards`]);
    else if (node.gate) add(3, null, 'job', 'current job', [node.gate.currentRun ?? 'idle']);
    else if (node.slots && node.slots.busy === 0) add(3, null, 'job', 'current job', ['idle']);
    else if (node.kind !== 'machine') missing(3, null, 'job', 'current job', reporting ? 'collect' : 'Running cards are reported by runner and gate hosts only.');
    if (node.kind !== 'machine') missing(3, null, 'deploy', 'last deploy', 'Deploys are recorded per project environment, not per node.');
    for (const { runner, prefix, label } of reports) {
      const events = runner?.reviewRestarts ?? [];
      events.slice(0, 3).forEach((event, index) => add(4, null, `${prefix}event:${index}`, `${label}${clock(event.restartedAt)}`,
        [`review daemon restarted · ${event.reviewsLost} reviews lost`, 'review daemon restarted', 'restart']));
      if (!events.length) missing(4, null, `${prefix}events`, `${label}recent events`, 'Events are not collected per node; only restarts of the review daemon are.');
    }
    missing(4, null, 'log', 'log lines', 'Log lines are not collected in the workspace picture.');
  }

  if (seenAt) add(4, null, 'seen', 'last report', [ago(seenAt, now) + ' ago']);

  // Card details only: how the Studio knows the node.
  add(5, null, 'technical', 'reported name', [node.technical]);
  add(5, null, 'reported', 'reported as', [[node.source?.facts?.machineKind, node.source?.facts?.virtualization].filter(Boolean).join(' · ')]);
  add(5, null, 'origin', 'known from', [node.source ? (node.undeclared ? `${node.source.sourceRef} (observed, not declared)` : node.source.sourceRef) : null]);
  add(5, null, 'health', 'health detail', [node.health.detail]);
  const tools = node.source?.facts?.warnings?.tools ?? [];
  add(5, null, 'tools', 'tools', [tools.map(tool => `${tool.name}${tool.version ? ` ${tool.version}` : ''}${tool.state === 'ok' ? '' : ` (${tool.state})`}`).join(', ')]);
  cache.set(node, facts);
  return facts;
}

/** The installation as a whole: one aggregate per layer, each the sum of what the canvas shows. */
function installationFacts(scene: LiveScene): LiveFact[] {
  const facts: LiveFact[] = [];
  const add = (layer: LiveLayer | null, key: string, label: string, value: string, tone?: LiveFact['tone']): void => {
    facts.push({ key, level: 5, layer, label, values: [value], observed: tone !== 'muted', ...(tone ? { tone } : {}) });
  };
  const { totals, backup } = scene;
  add('perf', 'slots', 'slots in use', totals.totalSlots ? `${totals.busySlots} of ${scene.partialCapacity ? 'at least ' : ''}${totals.totalSlots}` : NOT_OBSERVED, totals.totalSlots ? undefined : 'muted');
  add('perf', 'reporting', 'hosts reporting', String(totals.reporting));
  const links = scene.links.filter(link => link.layer === 'network');
  const down = links.filter(link => link.health.tone === 'danger').length, up = links.filter(link => link.health.tone === 'success').length;
  add('network', 'links', 'connections', links.length ? `${links.length} · ${up} up${down ? ` · ${down} down` : ''}` : 'none reported', down ? 'danger' : undefined);
  const disks = scene.order.flatMap(node => node.disks);
  const pressed = disks.filter(disk => diskPressure(disk)).length;
  add('storage', 'disks', 'disks', disks.length ? `${disks.length} reported${pressed ? ` · ${pressed} under pressure` : ''}` : NOT_OBSERVED, !disks.length ? 'muted' : pressed ? 'warning' : undefined);
  add('storage', 'data', 'data locations', backup.locations ? String(backup.locations) : NOT_OBSERVED, backup.locations ? undefined : 'muted');
  add('backup', 'state', 'backup', backup.observed ? backup.health.word.replace(/^Backups /, '') : NOT_OBSERVED,
    !backup.observed ? 'muted' : backup.health.tone === 'success' ? 'success' : backup.health.tone === 'danger' ? 'danger' : 'warning');
  add('backup', 'coverage', 'backed up', backup.locations ? `${backup.covered} of ${backup.locations} data locations` : NOT_OBSERVED,
    !backup.locations ? 'muted' : backup.covered === backup.locations ? 'success' : 'warning');
  const last = backup.overview?.lastGood;
  add('backup', 'last', 'last backup', !backup.overview ? NOT_OBSERVED : last ? `${ago(last.createdAt, scene.generatedAt)} ago · ${last.verification}` : 'none yet',
    !backup.overview ? 'muted' : last ? undefined : 'warning');
  add('backup', 'offhost', 'off-host copy', backup.overview ? backup.overview.offHost.state.replace('-', ' ') : NOT_OBSERVED,
    !backup.overview ? 'muted' : backup.overview.offHost.state === 'current' ? 'success' : 'warning');
  add(null, 'machines', 'machines and VMs', String(totals.machines));
  add(null, 'containers', 'containers', String(totals.containers));
  add(null, 'services', 'services', String(totals.services));
  add(null, 'read', 'last read', scene.generatedAt ? clock(scene.generatedAt) : NOT_OBSERVED, scene.generatedAt ? undefined : 'muted');
  return facts;
}

/**
 * The facts a node shows on the canvas at a level. Order: level by level, and
 * inside a level the active layers first, the one switched on last leading, so
 * zooming in with the backup layer on adds backup facts first. Values nobody
 * reports are not rows; `canvasGaps` says them as sentences with their reason.
 */
export function canvasFacts(node: LiveNode, scene: LiveScene, level: LiveLevel, active: readonly LiveLayer[]): LiveFact[] {
  if (level < 2) return [];
  const rank = (fact: LiveFact): number => fact.level * 1000 + (fact.band ? 0 : 100)
    + (fact.layer ? active.indexOf(fact.layer) : active.length) * 10;
  return nodeFacts(node, scene)
    .filter(fact => fact.observed && fact.level <= level && (fact.layer === null || active.includes(fact.layer)))
    .map((fact, index) => ({ fact, index })).sort((a, b) => rank(a.fact) - rank(b.fact) || a.index - b.index).map(item => item.fact);
}

const GAP_WHY: Readonly<Record<Exclude<LiveGapKind, 'source'>, { long: string; short: string }>> = {
  collect: { long: 'The Studio does not collect this per node yet.', short: 'not collected per node yet' },
  rights: { long: 'The backup status could not be read; it needs operate rights.', short: 'backup status not readable' },
  scope: { long: 'Data locations are listed for the Studio machine only.', short: 'listed for the Studio machine only' },
};

/** Why a node has no live values of its own, by what it is and how the Studio knows it. */
export function sourceGap(node: LiveNode): { long: string; short: string } {
  const chip = !!node.parent && (node.parent.kind === 'container' || node.parent.kind === 'service');
  if (node.kind === 'external') return { long: 'It lies outside the installation and the Studio does not probe it.', short: 'outside, not probed' };
  if (node.kind === 'target') return GAP_WHY.rights;
  if (node.kind === 'disk') return { long: 'No host on this machine reports its disks.', short: 'no disk report from this machine' };
  if (chip) return { long: 'Its host reports this service by name only.', short: 'reported by name only' };
  if (node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group') return node.source?.provenance === 'observed'
    ? { long: 'A host inside names this machine; nothing reports from the machine itself.', short: 'named by a host, no report of its own' }
    : { long: 'It is declared in the placement file and no host reports from it.', short: 'declared, no host reports from it' };
  return { long: 'No agent runs inside it; load and disk are reported for its host only.', short: 'no agent inside reports' };
}

/**
 * What a node cannot show at a level, grouped by reason (AGT-4024, owner feedback 2):
 * from the container level on a node says what is not observed and why, instead of
 * a row of "not observed" per value. The reason with the most values comes first.
 */
export function canvasGaps(node: LiveNode, scene: LiveScene, level: LiveLevel, active: readonly LiveLayer[]): LiveGap[] {
  // At the machine level a node says it only when it has nothing else to show, so no readable box stays empty.
  const bare = level === 2 && canvasFacts(node, scene, 2, active).length === 0;
  if (level < 3 && !bare) return [];
  const reach = Math.max(level, 3);
  const groups = new Map<LiveGapKind, string[]>();
  for (const fact of nodeFacts(node, scene)) {
    if (fact.observed || fact.level > reach || !(fact.layer === null || active.includes(fact.layer)) || !fact.gap) continue;
    groups.set(fact.gap, [...(groups.get(fact.gap) ?? []), fact.label]);
  }
  const source = sourceGap(node);
  return [...groups.entries()].sort((a, b) => b[1].length - a[1].length).map(([kind, labels]) => {
    const why = kind === 'source' ? source : GAP_WHY[kind];
    const list = labels.join(', '), count = `${labels.length} facts`;
    return { kind, labels, values: [`Not observed: ${list}. ${why.long}`, `Not observed: ${list} (${why.short}).`,
      ...(labels.length > 1 ? [`${count} not observed: ${why.short}.`] : []), `Not observed: ${why.short}.`] };
  });
}

/**
 * Level of a node. Containers, services and storage strips are judged by the size
 * they have on screen, frames by their header. A service drawn inside a container
 * follows its container, and the small boxes (a machine that holds nothing, an
 * external, the backup target) follow the scale a container would need, so at the
 * container level everything in view is at the container level.
 */
export function levelOf(node: LiveNode, width: number, height: number, scale: number): LiveLevel {
  if (isFrame(node) && node.children.length) {
    const head = node.headH * scale;
    return head >= 150 && width >= 760 ? 4 : head >= 98 && width >= 520 ? 3 : head >= 46 && width >= 300 ? 2 : 1;
  }
  if (node.kind === 'disk') return height >= 190 && width >= 620 ? 4 : height >= 120 && width >= 420 ? 3 : height >= 62 && width >= 240 ? 2 : 1;
  const parent = node.parent;
  if (parent && (parent.kind === 'container' || parent.kind === 'service')) return levelOf(parent, parent.rect.w * scale, parent.rect.h * scale, scale);
  if (node.kind !== 'container' && node.kind !== 'service') { width = LIVE_METRICS.unitW * scale; height = LIVE_METRICS.unitH * scale; }
  return width >= 470 && height >= 300 ? 4 : width >= 290 && height >= 190 ? 3 : width >= 160 && height >= 84 ? 2 : 1;
}

/** Lowest scale at which a node is at the container level. */
export function leastContainerScale(node: LiveNode): number {
  if (node.parent && (node.parent.kind === 'container' || node.parent.kind === 'service')) return leastContainerScale(node.parent);
  const small = node.kind !== 'container' && node.kind !== 'service' && node.kind !== 'disk';
  const w = small ? LIVE_METRICS.unitW : node.rect.w, h = small ? LIVE_METRICS.unitH : node.rect.h;
  return (node.kind === 'disk' ? Math.max(420 / w, 120 / h) : Math.max(290 / w, 190 / h)) * 1.02;
}
/** Scale at which a node is at the container level with room for its facts: the scale a jump lands on. */
export const containerScale = (node: LiveNode): number => leastContainerScale(node) * (node.kind === 'disk' ? 1.12 : 1.4);
