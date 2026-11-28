import type { StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { observationWord, type StructureData, type StructureDataLocation, type StructureDiskFact, type StructureLink,
  type StructureNode, type StructureReleaseFacts } from '../structure.model';
import type { LiveBackupOverview, LiveGateWorker, LiveRunnerSnapshot, LiveSources } from './live-structure.sources';

/**
 * Scene of the live structure view (AGT-4024): the nodes and links the canvas,
 * the outline and the card details all read. It is built only from what the
 * Studio reports. A value nobody reports stays `null` and is shown as
 * "not observed"; nothing here is estimated or simulated.
 */
export interface LiveRect { x: number; y: number; w: number; h: number; }
export type LiveLayer = 'perf' | 'network' | 'storage' | 'backup' | 'warnings';
export type LiveKind = 'boundary' | 'machine' | 'vm' | 'group' | 'container' | 'service' | 'disk' | 'external' | 'target';
export const NOT_OBSERVED = 'not observed';

export const LIVE_LAYERS: readonly { id: LiveLayer; label: string; short: string; treatment: string }[] = [
  { id: 'perf', label: 'Performance', short: 'Perf', treatment: 'Meters and slot cells inside the nodes' },
  { id: 'network', label: 'Network', short: 'Network', treatment: 'Routes with ports, direction and state' },
  { id: 'storage', label: 'Storage', short: 'Storage', treatment: 'Segmented disk strips with the pressure mark' },
  { id: 'backup', label: 'Backup', short: 'Backup', treatment: 'Hatching on data without a copy and the route to the target' },
  { id: 'warnings', label: 'Warnings', short: 'Warnings', treatment: 'Pins on the nodes and tinted bands with the finding' },
];
export const LIVE_KIND_LABEL: Readonly<Record<LiveKind, string>> = {
  boundary: 'System boundary', machine: 'Machine', vm: 'Virtual machine', group: 'Group', container: 'Container',
  service: 'Service', disk: 'Storage', external: 'External', target: 'Backup target',
};

export interface LiveHealth { tone: StatusTone; word: string; detail: string | null; }
export interface LiveWarning { id: string; node: LiveNode; tone: 'warning' | 'danger'; title: string; short: string; detail: string; }
export interface LivePerf {
  cpu: number | null; cores: number | null; load: number[] | null; memUsed: number | null; memTotal: number | null;
  rx: number | null; tx: number | null; observedAt: string | null;
}
export interface LiveSlots { busy: number; total: number | null; cards: string[]; }
export interface LiveNode {
  id: string; kind: LiveKind;
  /** Display name: a role where the Studio knows one, never a host name. */
  name: string; short: string; role: string;
  /** Reported identifier (host id, container name). Shown in the card details only. */
  technical: string | null;
  parent: LiveNode | null; children: LiveNode[];
  health: LiveHealth; undeclared: boolean;
  source: StructureNode | null;
  perf: LivePerf | null; slots: LiveSlots | null;
  disks: StructureDiskFact[]; locations: StructureDataLocation[];
  release: StructureReleaseFacts | null; version: string | null;
  /** Exact process identity, or the sole runner of a host aggregate. */
  runner: LiveRunnerSnapshot | null;
  /** All runner identities represented by this node, sorted by runnerId. */
  runners: readonly LiveRunnerSnapshot[]; gate: LiveGateWorker | null;
  warnings: LiveWarning[]; links: LiveLink[];
  rect: LiveRect; headH: number;
}
export interface LiveLink {
  id: string; a: LiveNode; b: LiveNode;
  kind: string; style: 'call' | 'control' | 'tunnel' | 'backup';
  name: string; short: string;
  health: LiveHealth; latencyMs: number | null; transport: string | null; probedAt: string | null;
  arrow: boolean; broken: boolean; layer: 'network' | 'backup';
  source: StructureLink | null;
}
export interface LiveBackupSummary {
  observed: boolean; overview: LiveBackupOverview | null;
  health: LiveHealth; locations: number; covered: number; target: LiveNode | null;
}
export interface LiveScene {
  boundary: LiveNode; outside: LiveNode[]; order: LiveNode[]; byId: Map<string, LiveNode>;
  links: LiveLink[]; warnings: LiveWarning[]; backup: LiveBackupSummary;
  generatedAt: string | null; revision: number;
  totals: { machines: number; containers: number; services: number; busySlots: number; totalSlots: number; reporting: number };
}

const GIB = 1024 ** 3;
export const gib = (bytes: number): string => `${(bytes / GIB).toFixed(bytes >= 10 * GIB ? 0 : 1)} GiB`;
export const size = (bytes: number): string => bytes >= GIB ? gib(bytes)
  : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MiB` : bytes >= 1024 ? `${Math.round(bytes / 1024)} KiB` : `${Math.max(0, bytes)} B`;
export const rate = (bytes: number): string => bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MiB/s` : `${(bytes / 1024).toFixed(1)} KiB/s`;
export const percent = (fraction: number): string => `${Math.round(fraction * 100)}%`;
export const diskUsed = (disk: StructureDiskFact): number => disk.totalBytes > 0 ? disk.usedBytes / disk.totalBytes : 0;
/** Default disk pressure limits of the Studio (`DiskPressure:*`): warning and critical by free space. */
export function diskPressure(disk: StructureDiskFact): 'warning' | 'danger' | null {
  if (disk.totalBytes <= 0) return null;
  const freeShare = disk.freeBytes / disk.totalBytes;
  if (disk.freeBytes <= 8 * GIB || freeShare <= 0.05) return 'danger';
  return disk.freeBytes <= 20 * GIB || freeShare <= 0.15 ? 'warning' : null;
}
export function ago(iso: string | null | undefined, now: string | null): string | null {
  if (!iso) return null;
  const seconds = Math.max(0, ((now ? Date.parse(now) : Date.now()) - Date.parse(iso)) / 1000);
  if (!Number.isFinite(seconds)) return null;
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`;
  if (seconds < 172800) return `${Math.round(seconds / 3600)} h`;
  return `${Math.round(seconds / 86400)} d`;
}
export const COVERAGE_WORD: Readonly<Record<string, string>> = {
  both: 'backup and git remote', backup: 'backup', 'git-remote': 'git remote only', none: 'no copy' };

const FRAME = new Set(['physical', 'cloud-server', 'server', 'host', 'cluster', 'managed-runtime', 'location', 'region', 'environment', 'system', 'group']);
const NESTED = new Set(['vm', 'namespace']);
const KIND_WORD: Readonly<Record<string, string>> = {
  physical: 'Physical machine', 'cloud-server': 'Cloud server', server: 'Server', host: 'Host', vm: 'Virtual machine',
  cluster: 'Cluster', namespace: 'Namespace', 'managed-runtime': 'Managed runtime', container: 'Container', process: 'Host service',
  managed: 'Managed service', external: 'External service', environment: 'Environment', location: 'Location', region: 'Region',
  system: 'System', unit: 'Deployable unit', registry: 'Registry',
};
const ROLE_WORD: Readonly<Record<string, string>> = { coding: 'coding', review: 'review', gate: 'gate', api: 'API', web: 'web', edge: 'edge', engine: 'engine' };
const sentence = (text: string): string => text ? text[0].toUpperCase() + text.slice(1) : text;
const joinRoles = (roles: readonly string[]): string => {
  const words = roles.map(role => ROLE_WORD[role] ?? role);
  return words.length > 1 ? `${words.slice(0, -1).join(', ')} and ${words.at(-1)}` : words[0] ?? '';
};

export function healthOf(source: Pick<StructureNode, 'observation'> | Pick<StructureLink, 'observation'>): LiveHealth {
  const word = observationWord(source.observation);
  const tone: StatusTone = word === 'Up' ? 'success' : word === 'Down' ? 'danger'
    : word === 'Degraded' || word === 'Differs' || word === 'Stale' ? 'warning' : 'neutral';
  return { tone, word, detail: source.observation?.detail ?? null };
}

function blank(id: string, kind: LiveKind, name: string, role: string, parent: LiveNode | null): LiveNode {
  return { id, kind, name, short: name, role, technical: null, parent, children: [], health: { tone: 'neutral', word: 'Not observed', detail: null },
    undeclared: false, source: null, perf: null, slots: null, disks: [], locations: [], release: null, version: null,
    runner: null, runners: [], gate: null, warnings: [], links: [], rect: { x: 0, y: 0, w: 0, h: 0 }, headH: 0 };
}

/** Kind on the canvas: frames hold other nodes, units are the things that run. */
function liveKind(node: StructureNode, parent: StructureNode | null, hasChildren: boolean, parentIsUnit: boolean): LiveKind {
  if (node.kind === 'boundary') return 'boundary';
  if (node.kind === 'external' || node.kind === 'managed' || node.kind === 'registry') return 'external';
  if (parentIsUnit) return 'service';
  if (NESTED.has(node.kind)) return 'vm';
  const top = !parent || parent.kind === 'boundary' || parent.kind === 'location' || parent.kind === 'region' || parent.kind === 'environment' || parent.kind === 'system';
  if (FRAME.has(node.kind) && (top || node.kind !== 'host')) return node.kind === 'physical' || node.kind === 'cloud-server' || node.kind === 'server' || node.kind === 'host' ? 'machine' : 'group';
  if (node.id.startsWith('machine:') && top) return 'machine';
  if (node.kind === 'container') return 'container';
  // A reporting host inside another machine and a bare process are services of that machine.
  return hasChildren && node.kind !== 'host' && node.kind !== 'process' ? 'group' : 'service';
}

function displayName(node: StructureNode, kind: LiveKind, roles: readonly string[], descendantRoles: readonly string[]): string {
  if (node.kind === 'boundary') return node.label === 'Installation' ? 'Agent Studio installation' : node.label;
  const reporting = !!node.facts?.hostId;
  if (reporting && roles.length) return `${sentence(joinRoles(roles))} ${kind === 'container' ? 'host' : kind === 'machine' ? 'machine' : kind === 'vm' ? 'VM' : 'runner'}`;
  if (node.provenance === 'observed' && (kind === 'machine' || kind === 'vm') && node.sourceRef !== 'studio self observation' && descendantRoles.length)
    return `${sentence(joinRoles(descendantRoles))} ${kind === 'vm' ? 'VM' : 'machine'}`;
  return sentence(node.label);
}

function roleLine(node: StructureNode, kind: LiveKind, roles: readonly string[]): string {
  const facts = node.facts;
  const parts: string[] = [];
  if (node.kind === 'boundary') return 'self-hosted';
  if (kind === 'service' && node.kind === 'process') parts.push(node.role ? `${ROLE_WORD[node.role] ?? node.role} service` : 'host service');
  else if (kind === 'service') parts.push('host service');
  else parts.push((KIND_WORD[node.kind] ?? node.kind).toLowerCase());
  if (node.role && node.kind !== 'process') parts.push(`${ROLE_WORD[node.role] ?? node.role} role`);
  if (facts?.virtualization && facts.virtualization !== 'none') parts.push(facts.virtualization);
  if (facts?.performance?.cpuCores) parts.push(`${facts.performance.cpuCores} cores`);
  if (facts?.capacity?.totalSlots != null) parts.push(`${facts.capacity.totalSlots} slots`);
  else if (roles.length && !facts?.performance?.cpuCores) parts.push(`${joinRoles(roles)} role`);
  return parts.join(' · ');
}

function warn(node: LiveNode, key: string, tone: 'warning' | 'danger', title: string, short: string, detail: string): void {
  node.warnings.push({ id: `${node.id}#${key}`, node, tone, title, short, detail });
}

function nodeWarnings(node: LiveNode, now: string | null): void {
  const source = node.source, facts = source?.facts;
  if (node.health.word === 'Down') warn(node, 'down', 'danger', `${node.name} is down`, 'down', node.health.detail ?? 'The last report says it is not running.');
  if (node.health.word === 'Stale') warn(node, 'stale', 'warning', `${node.name} stopped reporting`, 'no reports',
    `Last report ${ago(facts?.lastSeenAt ?? source?.observation?.observedAt, now) ?? 'at an unknown time'} ago.`);
  for (const disk of node.disks) {
    const level = diskPressure(disk);
    if (level) warn(node, `disk:${disk.mount}`, level, `Disk ${percent(diskUsed(disk))} full`, `disk ${percent(diskUsed(disk))} full`,
      `${disk.mount} (${disk.purpose}) has ${gib(disk.freeBytes)} free of ${gib(disk.totalBytes)}. The Studio warns at 20 GiB or 15% free and treats 8 GiB or 5% free as critical.`);
  }
  const warnings = facts?.warnings;
  if (!warnings) return;
  for (const key of warnings.missingRequired) warn(node, `missing:${key}`, 'danger', `Missing ${key}`, `missing ${key}`, 'A capability this host needs for its role is not installed. Cards that need it are not started here.');
  for (const tool of warnings.tools.filter(item => item.state === 'unhealthy'))
    warn(node, `unhealthy:${tool.name}`, 'warning', `${tool.name} is unhealthy`, `${tool.name} unhealthy`, 'The host reports this toolchain as present and not usable.');
  for (const name of warnings.behindRequired) {
    const tool = warnings.tools.find(item => item.name === name);
    warn(node, `behind:${name}`, 'warning', `${name} is behind`, `${name} behind`,
      tool?.version ? `Installed ${tool.version}${tool.targetVersion ? `, target ${tool.targetVersion}` : ''}.` : 'The installed version is below the target.');
  }
  if (warnings.releaseBehind) warn(node, 'release', 'warning', 'Runner release is behind', 'release behind',
    `Runs ${node.release?.version ?? 'an unreported version'}${node.release?.stableVersion ? `, stable is ${node.release.stableVersion}` : ''}.`);
  if (warnings.pendingHostInstall) warn(node, 'install', 'warning', 'Host install is pending', 'install pending', 'A newer runner release waits to be installed on this host.');
  for (const card of warnings.blockedCards) warn(node, `card:${card.key}`, 'warning', `${card.key} ${card.state}`, `${card.key} ${card.state}`, `Reason: ${card.reason}.`);
  if (warnings.linksDown.length) warn(node, 'links', 'danger', `${warnings.linksDown.length} ${warnings.linksDown.length === 1 ? 'link is' : 'links are'} down`, 'link down',
    'A connection this host needs does not answer.');
}

function backupHealth(overview: LiveBackupOverview | null): LiveHealth {
  if (!overview) return { tone: 'neutral', word: 'Not observed', detail: 'The backup status could not be read.' };
  const word = overview.state === 'ok' ? 'Backups current' : overview.state === 'warning' ? 'Backups need attention'
    : overview.state === 'failing' ? 'Backups failing' : overview.state === 'disabled' ? 'Backups disabled' : 'Backups not configured';
  return { tone: overview.state === 'ok' ? 'success' : overview.state === 'failing' ? 'danger' : 'warning', word,
    detail: overview.warnings[0]?.message ?? null };
}

function linkStyle(link: StructureLink): LiveLink['style'] {
  const text = `${link.kind} ${link.facts?.transport ?? ''}`.toLowerCase();
  if (/tunnel|ssh|reverse|vpn|overlay|mesh/.test(text)) return 'tunnel';
  return link.kind === 'control' ? 'control' : 'call';
}

/** Builds the scene. `sources` are the optional reads; each may be `null` when it could not be read. */
export function buildLiveScene(data: StructureData, sources: LiveSources): LiveScene {
  const now = data.generatedAt ?? null;
  const sourceById = new Map(data.nodes.map(node => [node.id, node]));
  const childrenOf = new Map<string, StructureNode[]>();
  for (const node of data.nodes) if (node.parentId && sourceById.has(node.parentId))
    childrenOf.set(node.parentId, [...(childrenOf.get(node.parentId) ?? []), node]);
  const rolesBelow = (node: StructureNode): string[] => [...new Set([...(node.facts?.roles ?? []),
    ...(childrenOf.get(node.id) ?? []).flatMap(rolesBelow)])].sort();
  const byId = new Map<string, LiveNode>();
  const order: LiveNode[] = [];
  const runnerById = new Map<string, LiveRunnerSnapshot>();
  const runnersByHost = new Map<string, LiveRunnerSnapshot[]>();
  for (const runner of sources.runners ?? []) {
    runnerById.set(runner.runnerId.toLowerCase(), runner);
    const hostId = runner.hostId.toLowerCase();
    runnersByHost.set(hostId, [...(runnersByHost.get(hostId) ?? []), runner]);
  }
  for (const runners of runnersByHost.values()) runners.sort((a, b) => a.runnerId.localeCompare(b.runnerId));

  const add = (source: StructureNode, parent: LiveNode | null): LiveNode => {
    const kids = childrenOf.get(source.id) ?? [];
    const parentSource = source.parentId ? sourceById.get(source.parentId) ?? null : null;
    const parentIsUnit = !!parent && (parent.kind === 'container' || parent.kind === 'service');
    const kind = liveKind(source, parentSource, kids.length > 0, parentIsUnit);
    const roles = source.facts?.roles ?? [];
    const node = blank(source.id, kind, displayName(source, kind, roles, rolesBelow(source)), roleLine(source, kind, roles), parent);
    node.source = source;
    node.technical = source.facts?.hostId ?? source.facts?.containerName ?? source.detail ?? (node.name !== sentence(source.label) && source.kind !== 'boundary' ? source.label : null);
    node.health = healthOf(source);
    node.undeclared = source.provenance === 'observed' || source.observation?.difference === 'undeclared';
    const performance = source.facts?.performance, observation = source.observation;
    const cpu = performance?.cpuPercent ?? observation?.cpuPercent ?? null;
    const memUsed = performance?.memoryUsedBytes ?? observation?.memoryUsedBytes ?? null;
    if (performance || cpu != null || memUsed != null) node.perf = {
      cpu: cpu != null ? cpu / 100 : null, cores: performance?.cpuCores ?? null,
      load: performance?.load1 != null ? [performance.load1, performance.load5, performance.load15].filter((v): v is number => v != null) : null,
      memUsed, memTotal: performance?.memoryTotalBytes ?? observation?.memoryTotalBytes ?? null,
      rx: performance?.networkReceiveBytesPerSecond ?? null, tx: performance?.networkTransmitBytesPerSecond ?? null,
      observedAt: performance?.observedAt ?? observation?.observedAt ?? null };
    const capacity = source.facts?.capacity;
    if (capacity && (capacity.totalSlots != null || capacity.busySlots > 0))
      node.slots = { busy: capacity.busySlots, total: capacity.totalSlots ?? null, cards: capacity.runningCards ?? [] };
    node.release = source.facts?.release ?? null;
    node.version = node.release?.version ?? observation?.version ?? null;
    // A declared unit is an identity, even if its snapshot is currently absent.
    // Only host aggregates may use the hostId join; a process never borrows a peer.
    const runner = source.unitId ? runnerById.get(source.unitId.toLowerCase()) : null;
    node.runners = source.unitId ? (runner ? [runner] : [])
      : source.kind !== 'process' && source.facts?.hostId ? runnersByHost.get(source.facts.hostId.toLowerCase()) ?? [] : [];
    node.runner = node.runners.length === 1 ? node.runners[0] : null;
    node.gate = source.facts?.hostId ? (sources.environment?.gateWorkers ?? []).find(worker => worker.id === source.facts!.hostId || worker.name === source.facts!.hostId) ?? null : null;
    byId.set(node.id, node);
    order.push(node);
    const frame = kind === 'machine' || kind === 'vm' || kind === 'group' || kind === 'boundary';
    const disks = performance?.disks ?? [], locations = source.facts?.dataLocations ?? [];
    if (!frame) { node.disks = disks; node.locations = locations; }
    for (const child of kids) if (child.kind !== 'external') node.children.push(add(child, node));
    if (frame && kind !== 'boundary' && (disks.length || locations.length)) {
      const disk = blank(`${source.id}#storage`, 'disk', 'Storage', `${disks.length} ${disks.length === 1 ? 'disk' : 'disks'}${locations.length ? ` · ${locations.length} data locations` : ''}`, node);
      disk.short = 'Storage';
      disk.disks = disks; disk.locations = locations;
      disk.health = { tone: disks.some(item => diskPressure(item) === 'danger') ? 'danger' : disks.some(item => diskPressure(item)) ? 'warning' : disks.length ? 'success' : 'neutral',
        word: disks.length ? `${percent(Math.max(...disks.map(diskUsed)))} used` : 'Not observed', detail: null };
      node.children.push(disk);
      byId.set(disk.id, disk);
      order.push(disk);
    }
    return node;
  };

  const root = data.nodes.find(node => node.kind === 'boundary');
  const boundary = root ? add(root, null) : blank('installation', 'boundary', 'Agent Studio installation', 'self-hosted', null);
  if (!root) { byId.set(boundary.id, boundary); order.push(boundary); }
  // Anything without a place inside the boundary (older reports, other roots) still shows, inside it.
  for (const node of data.nodes) if (!byId.has(node.id) && node.kind !== 'external' && (!node.parentId || !sourceById.has(node.parentId)))
    boundary.children.push(add(node, boundary));
  const outside = data.nodes.filter(node => node.kind === 'external' && !byId.has(node.id)).map(node => add(node, null));
  numberSiblings(order);
  for (const node of order) nodeWarnings(node, now);

  // Backup: the status the Studio reports, the coverage per data location, and the off-host target.
  const overview = sources.backup;
  const holder = order.find(node => node.kind === 'disk' && node.locations.length) ?? null;
  const present = holder?.locations.filter(location => location.exists) ?? [];
  const backup: LiveBackupSummary = { observed: !!overview, overview, health: backupHealth(overview), locations: present.length,
    covered: present.filter(location => location.coverage === 'backup' || location.coverage === 'both').length, target: null };
  const links: LiveLink[] = [];
  if (holder) {
    const state = overview?.offHost.state ?? null;
    const target = blank('backup-target', 'target', 'Off-host backup target', !overview ? NOT_OBSERVED : state === 'not-configured' ? 'not configured' : overview.offHost.kind || 'configured', null);
    target.short = 'Backup target';
    target.health = !overview ? { tone: 'neutral', word: 'Not observed', detail: 'The backup status could not be read.' }
      : state === 'current' ? { tone: 'success', word: 'Current', detail: null }
      : state === 'failed' || state === 'invalid' ? { tone: 'danger', word: state === 'failed' ? 'Upload failed' : 'Target invalid', detail: overview.offHost.lastError }
      : state === 'not-configured' ? { tone: 'warning', word: 'Not configured', detail: 'No copy leaves the host that holds the data.' }
      : { tone: 'neutral', word: state === 'pending' ? 'Upload pending' : 'Idle', detail: null };
    backup.target = target;
    outside.push(target);
    byId.set(target.id, target);
    order.push(target);
    links.push({ id: 'link:backup', a: holder, b: target, kind: 'backup', style: 'backup', name: 'Off-host backup', short: state === 'current' ? 'backup copy' : 'no off-host copy',
      health: target.health, latencyMs: null, transport: overview?.offHost.kind ?? null, probedAt: overview?.offHost.lastUploadedAt ?? null,
      arrow: state === 'current', broken: state !== 'current' && state !== 'pending' && state !== 'idle', layer: 'backup', source: null });
    if (overview && overview.state !== 'ok') warn(holder, 'backup', overview.state === 'failing' ? 'danger' : 'warning', backup.health.word, backup.health.word.toLowerCase(),
      overview.warnings.map(item => item.message).join(' ') || 'The backup status reports this state without a message.');
    const bare = present.filter(location => location.coverage === 'none');
    if (bare.length) warn(holder, 'uncovered', 'warning', `${bare.length} data ${bare.length === 1 ? 'location has' : 'locations have'} no copy`, `${bare.length} without a copy`,
      `${bare.map(location => location.label).join(', ')}: neither a backup source nor a git remote covers ${bare.length === 1 ? 'it' : 'them'}.`);
  }

  for (const source of data.links) {
    const a = byId.get(source.from), b = byId.get(source.to);
    if (!a || !b || a === b) continue;
    const style = linkStyle(source);
    links.push({ id: source.id ?? `link:${source.from}>${source.to}:${source.kind}`, a, b, kind: source.kind, style, name: source.label,
      short: style === 'tunnel' ? 'tunnel' : source.kind, health: source.facts?.state ? linkHealth(source.facts.state) : healthOf(source),
      latencyMs: source.facts?.latencyMs ?? null, transport: source.facts?.transport ?? null, probedAt: source.facts?.probedAt ?? null,
      arrow: source.kind === 'call' || source.kind === 'artifact' || source.kind === 'publish' || source.kind === 'control', broken: false, layer: 'network', source });
  }
  for (const link of links) { link.a.links.push(link); link.b.links.push(link); }

  const open = order.reduce((sum, node) => sum + node.warnings.length, 0);
  boundary.health = open ? { tone: 'warning', word: `${open} open ${open === 1 ? 'warning' : 'warnings'}`, detail: null }
    : { tone: 'success', word: 'No open warnings', detail: null };
  const capacityNodes = order.filter(node => node.slots);
  // The projection repeats each runner's capacity on its host aggregate and process.
  // Keep both for drill-down, but count the host once, regardless of placement.
  const hostAggregates = new Map<string, LiveNode>();
  for (const node of capacityNodes) {
    const hostId = node.source?.facts?.hostId;
    if (hostId && node.id === `machine:${hostId}`) hostAggregates.set(hostId, node);
  }
  const units = capacityNodes.filter(node => {
    const aggregate = hostAggregates.get(node.source?.facts?.hostId ?? '');
    return !aggregate || aggregate === node;
  });
  const reportingHosts = new Set(order.filter(node => node.source?.facts?.hostId && node.health.word === 'Up')
    .map(node => node.source!.facts!.hostId!));
  const scene: LiveScene = { boundary, outside, order, byId, links, warnings: order.flatMap(node => node.warnings), backup,
    generatedAt: now, revision: data.revision ?? 0,
    totals: { machines: order.filter(node => node.kind === 'machine' || node.kind === 'vm').length,
      containers: order.filter(node => node.kind === 'container').length, services: order.filter(node => node.kind === 'service').length,
      busySlots: units.reduce((sum, node) => sum + node.slots!.busy, 0), totalSlots: units.reduce((sum, node) => sum + (node.slots!.total ?? 0), 0),
      reporting: reportingHosts.size } };
  return scene;
}

function linkHealth(state: string): LiveHealth {
  const tone: StatusTone = state === 'up' ? 'success' : state === 'down' ? 'danger' : state === 'degraded' || state === 'reconnecting' || state === 'connecting' ? 'warning' : 'neutral';
  return { tone, word: sentence(state), detail: null };
}

/** Siblings that would share a display name get a number, so "Review host 1" and "Review host 2" stay apart. */
function numberSiblings(order: LiveNode[]): void {
  const groups = new Map<string, LiveNode[]>();
  for (const node of order) {
    if (!node.source?.facts?.hostId) continue;
    const key = `${node.parent?.id ?? ''}\n${node.name}`;
    groups.set(key, [...(groups.get(key) ?? []), node]);
  }
  for (const group of groups.values()) {
    if (group.length < 2) continue;
    group.forEach((node, index) => {
      const reported = /(\d+)$/.exec(node.source!.label)?.[1];
      node.name = `${node.name} ${reported ?? index + 1}`;
      node.short = node.name;
    });
  }
}

export const ancestors = (node: LiveNode): LiveNode[] => { const out: LiveNode[] = []; for (let n = node.parent; n; n = n.parent) out.push(n); return out; };
export const pathOf = (node: LiveNode): LiveNode[] => [...ancestors(node).reverse(), node];
export function descendants(node: LiveNode, out: LiveNode[] = []): LiveNode[] { for (const child of node.children) { out.push(child); descendants(child, out); } return out; }
export const subtreeWarnings = (node: LiveNode): LiveWarning[] => [node, ...descendants(node)].flatMap(item => item.warnings);
/** What a frame holds, in words: "4 containers · 2 services". */
export function runsHere(node: LiveNode): string[] {
  const all = descendants(node);
  const count = (kind: LiveKind, one: string, many: string): string | null => {
    const n = all.filter(item => item.kind === kind).length;
    return n ? `${n} ${n === 1 ? one : many}` : null;
  };
  return [count('vm', 'VM', 'VMs'), count('container', 'container', 'containers'), count('service', 'service', 'services')].filter((v): v is string => !!v);
}
