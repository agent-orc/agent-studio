import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
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
    return LIVE_LAYERS.map(layer => ({
      layer: layer.id, title: layer.label, on: layers[layer.id], empty: EMPTY[layer.id],
      // Routes and findings have their own rows; the summary lines of the canvas are left to them.
      rows: facts.filter(fact => fact.layer === layer.id && !fact.key.startsWith('link:') && !fact.key.startsWith('warning:')
        && !fact.key.startsWith('detail:') && !(layer.id === 'warnings' && fact.key === 'inside')),
      links: layer.id === 'network' ? subject.links.filter(link => link.layer === 'network')
        : layer.id === 'backup' ? subject.links.filter(link => link.layer === 'backup') : [],
      warnings: layer.id === 'warnings' ? warnings : [],
    }));
  });
  readonly inside = computed(() => this.isRoot() ? [] : subtreeWarnings(this.subject()).filter(warning => warning.node !== this.subject()));
  readonly children = computed(() => this.isRoot() ? [] : descendants(this.subject()).map(node => ({ node, depth: pathOf(node).length - pathOf(this.subject()).length - 1 })));
  readonly kpis = computed(() => {
    const scene = this.scene(), totals = scene.totals;
    return [
      { label: 'Slots in use', value: totals.totalSlots ? String(totals.busySlots) : 'not observed', unit: totals.totalSlots ? `of ${totals.totalSlots}` : '' },
      { label: 'Hosts reporting', value: String(totals.reporting), unit: '' },
      { label: 'Open warnings', value: String(scene.warnings.length), unit: '' },
    ];
  });

  peer(link: LiveLink): LiveNode { return link.a === this.subject() ? link.b : link.a; }
  linkValue(link: LiveLink): string {
    return [link.latencyMs != null ? `${link.latencyMs.toFixed(1)} ms` : null, link.transport].filter(Boolean).join(' · ');
  }
  probed(link: LiveLink): string {
    const value = ago(link.probedAt, this.scene().generatedAt);
    return value ? `${value} ago` : 'not observed';
  }
}
