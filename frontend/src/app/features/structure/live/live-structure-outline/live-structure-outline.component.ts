import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output } from '@angular/core';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { LiveGlyphComponent } from '../live-glyph/live-glyph.component';
import { LIVE_KIND_LABEL, gib, type LiveNode, type LiveScene } from '../live-structure.model';

interface OutlineRow { node: LiveNode; depth: number; warnings: number; value: string; }

/**
 * The structure as a list: every node of the canvas, nested as it is drawn. A
 * click (or Enter) on a row selects the node and brings it into view on the
 * canvas at the container level; the row of the canvas selection is marked and
 * kept in view. It is also the keyboard and screen reader way into the canvas.
 */
@Component({
  selector: 'app-live-structure-outline',
  standalone: true,
  imports: [LiveGlyphComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-outline.component.html',
  styleUrl: './live-structure-outline.component.scss',
})
export class LiveStructureOutlineComponent {
  private readonly element = inject(ElementRef) as ElementRef<HTMLElement>;
  readonly scene = input.required<LiveScene>();
  readonly selected = input<string | null>(null);
  /** A row was chosen: the canvas selects the node and goes to it. */
  readonly jump = output<string>();
  readonly kindLabel = LIVE_KIND_LABEL;

  readonly rows = computed<OutlineRow[]>(() => {
    const rows: OutlineRow[] = [];
    const visit = (node: LiveNode, depth: number): void => {
      rows.push({ node, depth, warnings: node.warnings.length, value: node.slots ? `${node.slots.busy}/${node.slots.total ?? '?'}`
        : node.perf?.cpu != null ? `cpu ${Math.round(node.perf.cpu * 100)}%` : node.perf?.memUsed != null ? gib(node.perf.memUsed) : node.source?.facts?.device?.sizeBytes ? gib(node.source.facts.device.sizeBytes)
        : node.children.length ? `${node.children.length} inside` : node.health.word });
      for (const child of node.children) visit(child, depth + 1);
    };
    const scene = this.scene();
    for (const child of scene.boundary.children) visit(child, 0);
    for (const node of scene.outside) visit(node, 0);
    return rows;
  });

  constructor() {
    // Keep the row of the canvas selection in view.
    effect(() => {
      const id = this.selected();
      this.rows();
      if (!id) return;
      queueMicrotask(() => [...this.element.nativeElement.querySelectorAll<HTMLElement>('[data-outline-id]')]
        .find(row => row.dataset['outlineId'] === id)?.scrollIntoView?.({ block: 'nearest' }));
    });
  }

  label(row: OutlineRow): string {
    return `${row.node.name}, ${this.kindLabel[row.node.kind]}, ${row.node.health.word}`
      + (row.warnings ? `, ${row.warnings} ${row.warnings === 1 ? 'warning' : 'warnings'}` : '');
  }
}
