import { ChangeDetectionStrategy, Component, ElementRef, computed, input, output, signal, viewChild } from '@angular/core';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { LiveGlyphComponent } from '../live-glyph/live-glyph.component';
import { LIVE_KIND_LABEL, pathOf, type LiveNode, type LiveScene } from '../live-structure.model';
import { searchNodes } from '../live-structure.navigation';

/**
 * Jump field of the live structure view (AGT-4024, owner feedback 3): type a
 * name, a role or a kind, pick a node, and the canvas brings it into view at
 * the container level. A combobox with a list of matches; the first match is
 * taken with Enter.
 */
@Component({
  selector: 'app-live-structure-jump',
  standalone: true,
  imports: [LiveGlyphComponent, StatusIndicatorComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live-structure-jump.component.html',
  styleUrl: './live-structure-jump.component.scss',
})
export class LiveStructureJumpComponent {
  readonly scene = input.required<LiveScene>();
  readonly jump = output<string>();
  /** The field was left with Escape or after a jump; the host puts the focus back on the canvas. */
  readonly left = output<void>();
  readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');
  readonly query = signal('');
  readonly open = signal(false);
  readonly active = signal(0);
  readonly kindLabel = LIVE_KIND_LABEL;

  readonly matches = computed(() => searchNodes(this.scene(), this.query(), LIVE_KIND_LABEL));
  readonly listed = computed(() => this.open() && this.query().trim().length > 0);

  focus(): void { this.field().nativeElement.focus(); this.field().nativeElement.select(); }
  where(node: LiveNode): string {
    return pathOf(node).slice(1, -1).map(item => item.name).join(' / ') || (node.parent ? 'Installation' : 'Outside the installation');
  }
  onInput(value: string): void { this.query.set(value); this.active.set(0); this.open.set(true); }
  pick(node: LiveNode | undefined): void {
    if (!node) return;
    this.query.set('');
    this.open.set(false);
    this.jump.emit(node.id);
    this.left.emit();
  }
  onKeydown(event: KeyboardEvent): void {
    const count = this.matches().length;
    if (event.key === 'ArrowDown' && count) this.active.update(index => (index + 1) % count);
    else if (event.key === 'ArrowUp' && count) this.active.update(index => (index - 1 + count) % count);
    else if (event.key === 'Enter') this.pick(this.matches()[this.active()]);
    else if (event.key === 'Escape') { this.query.set(''); this.open.set(false); this.left.emit(); }
    else return;
    event.preventDefault();
    event.stopPropagation();
  }
}
