import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { DisclosureMarkerComponent } from '../../../../../components/disclosure-marker/disclosure-marker.component';
import type { ProtocolVerdict } from '../protocol-verdict';
/**
 * The single run outcome banner plus its raw-signal disclosure.
 *
 * ADM-17: the status line is the one disclosure control here. It owns the
 * shared marker, the hit area and `aria-expanded`; there is no second
 * "Why this status?" row duplicating the same expansion.
 */
@Component({
  selector: 'app-protocol-verdict-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, DisclosureMarkerComponent],
  templateUrl: './protocol-verdict-banner.component.html',
  styleUrl: './protocol-verdict-banner.component.scss',
})
export class ProtocolVerdictBannerComponent {
  readonly verdict = input.required<ProtocolVerdict>();
  readonly running = input(false);
  readonly canRequestInterim = input(false);
  readonly interimInFlight = input(false);
  readonly interimElapsedSeconds = input(0);

  readonly requestInterim = output<void>();

  /** Whether the reason and raw signals are shown in full. */
  readonly expanded = signal(false);
  readonly dismissed = signal(false);

  readonly hasSignals = computed<boolean>(() => (this.verdict().signals?.length ?? 0) > 0);

  readonly expandable = computed<boolean>(() => {
    const v = this.verdict();
    return this.hasSignals() || (v.detail?.length ?? 0) > 64;
  });

  constructor() {
    // Collapse again whenever the verdict content changes (task switch or a
    // fresh summary) so a long reason does not leave the next banner expanded.
    let previousKey = '';
    effect(() => {
      const v = this.verdict();
      const key = `${v.label}::${v.detail}`;
      if (key !== previousKey) {
        previousKey = key;
        this.expanded.set(false);
        this.dismissed.set(false);
      }
    });
  }

  toggle(): void {
    this.expanded.update((x) => !x);
  }

  dismiss(): void {
    this.dismissed.set(true);
  }
}
