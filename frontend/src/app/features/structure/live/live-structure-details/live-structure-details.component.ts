import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { LiveGlyphComponent } from '../live-glyph/live-glyph.component';
import { LIVE_LEVELS, nodeFacts, type LiveFact } from '../live-structure.facts';
import { LIVE_KIND_LABEL, LIVE_LAYERS, ago, descendants, pathOf, subtreeWarnings,
  type LiveLayer, type LiveLink, type LiveNode, type LiveScene, type LiveWarning } from '../live-structure.model';

interface DetailSection { layer: LiveLayer; title: string; on: boolean; rows: LiveFact[]; links: LiveLink[]; warnings: LiveWarning[]; empty: string; }
const EMPTY: Readonly<Record<LiveLayer, string>> = {
  perf: 'No load or capacity is reported for this node.',
  network: 'No connection of this node is reported.',
  storage: 'No disk or data location is reported for this node.',
  backup: 'No data location to back up is reported for this node.',
  warnings: 'No open finding here.',
};

/**
 * Card details: the complete list of facts of the selected node, or of the
 * installation when nothing is selected. Each of the five layers has its own
 * section with its sign, whether or not the layer is switched on, so a fact is
 * never lost because a layer is off. Missing values read "not observed".
 */
@Component({
  selector: 'app-live-structure-details',
  standalone: true,
  imports: [LiveGlyphComponent, StatusIndicatorComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-details.component.html',
  styleUrl: './live-structure-details.component.scss',
})
export class LiveStructureDetailsComponent {
  readonly scene = input.required<LiveScene>();
  readonly node = input<LiveNode | null>(null);
  readonly link = input<LiveLink | null>(null);
  readonly layers = input.required<Readonly<Record<LiveLayer, boolean>>>();
  /** Level the node currently has on the canvas, shown next to the level table. */
  readonly level = input<number | null>(null);
  readonly choose = output<string>();
  readonly chooseLink = output<string>();
  readonly zoom = output<string>();
  readonly openCard = output<string>();
  readonly closed = output<void>();
  readonly kindLabel = LIVE_KIND_LABEL;
  readonly levels = LIVE_LEVELS;
  readonly layerList = LIVE_LAYERS;
  readonly legend = [['machine', 'machine'], ['vm', 'virtual machine'], ['group', 'group'], ['container', 'container'], ['service', 'service'],
    ['disk', 'storage'], ['external', 'outside'], ['target', 'backup target'], ['boundary', 'system boundary']] as const;

  /** The node the card describes: the selection, else the installation. */
  readonly subject = computed(() => this.node() ?? this.scene().boundary);
  readonly isRoot = computed(() => this.subject().kind === 'boundary');
  readonly path = computed(() => pathOf(this.subject()).slice(0, -1).map(node => node.kind === 'boundary' ? 'Installation' : node.name).join(' / ')
    || (this.subject().kind === 'boundary' ? '' : 'Outside the installation'));
  private readonly facts = computed(() => nodeFacts(this.subject(), this.scene()));
  readonly general = computed(() => this.facts().filter(fact => fact.layer === null));
  readonly sections = computed<DetailSection[]>(() => {
    const subject = this.subject(), facts = this.facts(), layers = this.layers();
    const warnings = this.isRoot() ? this.scene().warnings : subject.warnings;
    const layersInOrder = this.isRoot() ? [...LIVE_LAYERS].sort((a, b) => Number(b.id === 'warnings') - Number(a.id === 'warnings')) : LIVE_LAYERS;
    return layersInOrder.map(layer => ({
      layer: layer.id, title: layer.label, on: layers[layer.id], empty: EMPTY[layer.id],
      // Routes and findings have their own rows; the summary lines of the canvas are left to them.
      rows: facts.filter(fact => fact.layer === layer.id && !fact.key.startsWith('link:') && !fact.key.startsWith('warning:')
        && !fact.key.startsWith('detail:') && !(layer.id === 'warnings' && fact.key === 'inside')),
      links: layer.id === 'network' ? (this.isRoot() ? this.scene().links : subject.links).filter(link => link.layer === 'network')
        : layer.id === 'backup' ? subject.links.filter(link => link.layer === 'backup') : [],
      warnings: layer.id === 'warnings' ? warnings : [],
    }));
  });
  readonly inside = computed(() => this.isRoot() ? [] : subtreeWarnings(this.subject()).filter(warning => warning.node !== this.subject()));
  readonly children = computed(() => this.isRoot() ? [] : descendants(this.subject()).map(node => ({ node, depth: pathOf(node).length - pathOf(this.subject()).length - 1 })));
  readonly slotCells = computed(() => {
    const node = this.subject(), slots = node.slots;
    if (!slots) return [];
    const cap = node.source?.facts?.capacity;
    return Array.from({ length: slots.total ?? slots.busy }, (_, index) => ({
      card: index < slots.busy ? slots.cards[index] : undefined,
      state: index < slots.busy ? 'In use' : index < slots.busy + (cap?.freeSlots ?? 0) ? 'Free'
        : index >= (slots.total ?? 0) - (cap?.heldSlots ?? 0) ? 'Held back' : 'Unreported',
    }));
  });
  readonly trends = computed(() => {
    const samples = this.scene().history?.trends ?? [];
    return (['busySlots', 'queuedTasks'] as const).map(key => {
      const peak = Math.max(1, ...samples.map(point => point[key] ?? 0));
      let connected = false;
      const path = samples.map((point, index) => {
        const value = point[key];
        if (value == null) { connected = false; return ''; }
        const command = connected ? 'L' : 'M';
        connected = true;
        const x = 1 + 98 * index / Math.max(1, samples.length - 1);
        return `${command}${x},${22 - 20 * value / peak}h0.1`;
      }).join(' ').trim();
      return { label: key === 'busySlots' ? 'Busy slots' : 'Queued tasks', path, value: samples.at(-1)?.[key] };
    });
  });
  age(time: string): string { return ago(time, this.scene().generatedAt) ?? 'unknown'; }
  readonly summary = computed(() => {
    const { warnings, totals, queued, partialCapacity } = this.scene();
    const slots = partialCapacity ? `${totals.busySlots} slots in use (limit partially observed)`
      : totals.totalSlots ? `${totals.busySlots} of ${totals.totalSlots} slots` : 'slots not observed';
    return `${warnings.length} open ${warnings.length === 1 ? 'warning' : 'warnings'}, ${slots}, ${queued == null ? 'queue not observed' : queued + ' queued'}`;
  });
  readonly kpis = computed(() => {
    const scene = this.scene(), totals = scene.totals;
    return [
      { label: 'Slots in use', value: totals.totalSlots ? String(totals.busySlots) : 'not observed', unit: totals.totalSlots ? `${scene.partialCapacity ? 'of at least' : 'of'} ${totals.totalSlots}` : '' },
      { label: 'Queued', value: scene.queued?.toString() ?? 'not observed', unit: '' },
      { label: 'Open warnings', value: String(scene.warnings.length), unit: '' },
    ];
  });

  peer(link: LiveLink): LiveNode { return link.a === this.subject() ? link.b : link.a; }
  endpoints(link: LiveLink): string {
    if (this.isRoot()) return link.a.name + ' to ' + link.b.name;
    return (link.a === this.subject() ? 'to ' : 'from ') + this.peer(link).name;
  }
  linkValue(link: LiveLink): string {
    return [link.latencyMs != null ? `${link.latencyMs.toFixed(1)} ms` : null, link.transport].filter(Boolean).join(' · ');
  }
  probed(link: LiveLink): string {
    const value = ago(link.probedAt, this.scene().generatedAt);
    return value ? `${value} ago` : 'not observed';
  }
}
