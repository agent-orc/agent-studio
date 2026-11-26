import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** How much a chip stands out: `strong` for the one value a reader must not miss, `quiet` for a derived or summarising value. */
export type ChipEmphasis = 'default' | 'strong' | 'quiet';

/**
 * The shared neutral chip: a short, sentence-case label for a value that is
 * not a state (a role, a scope, a project, a kind). A state uses
 * `<app-status-indicator>` instead.
 *
 * With a `detail` the chip has two segments, read as "label in detail":
 *
 *   <app-chip label="Developer" detail="Website / stage" />
 *
 * `note` adds a muted qualifier such as "via Team Website".
 */
@Component({
  selector: 'app-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chip.component.html',
  styleUrl: './chip.component.scss',
})
export class ChipComponent {
  readonly label = input.required<string>();
  readonly detail = input<string | null>(null);
  readonly note = input<string | null>(null);
  readonly emphasis = input<ChipEmphasis>('default');
  readonly testId = input<string | null>(null);

  /** The whole value as one string, for the tooltip of a chip that is cut off. */
  readonly full = computed(() => [this.label(), this.detail(), this.note()].filter(Boolean).join(' · '));
}
