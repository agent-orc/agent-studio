import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { OrchestratorContextReceipt, OrchestratorContextSourceReceipt } from '../../models/orchestrator.model';

/** Context source status to indicator tone; a status the backend adds later stays neutral. */
const CONTEXT_SOURCE_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  included: 'success',
  excerpted: 'warning',
  unresolved: 'danger',
  unavailable: 'danger',
  blocked: 'danger',
  oversize: 'danger',
  'omitted-budget': 'neutral',
};

export function contextSourceStatusTone(status: string): StatusTone {
  return CONTEXT_SOURCE_STATUS_TONES[status] ?? 'neutral';
}

@Component({
  selector: 'app-orchestrator-context-receipt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent],
  templateUrl: './orchestrator-context-receipt.component.html',
  styleUrl: './orchestrator-context-receipt.component.scss',
})
export class OrchestratorContextReceiptComponent {
  readonly receipt = input.required<OrchestratorContextReceipt>();
  readonly expanded = signal(false);
  readonly sources = computed(() => this.receipt().sources ?? []);
  readonly sourceCount = computed(() => this.sources().length || this.receipt().includedBlocks.length);
  readonly estimatedTokens = computed(() => this.receipt().budget?.estimatedIncludedTokens ?? null);

  toggle(): void {
    this.expanded.update(value => !value);
  }

  kindLabel(kind: string): string {
    return kind.replaceAll('-', ' ').replace(/\b\w/g, value => value.toUpperCase());
  }

  statusLabel(source: OrchestratorContextSourceReceipt): string {
    switch (source.status) {
      case 'included': return 'Included';
      case 'excerpted': return 'Excerpted';
      case 'unresolved': return 'Unresolved';
      case 'unavailable': return 'Unavailable';
      case 'blocked': return 'Blocked';
      case 'oversize': return 'Too large';
      case 'omitted-budget': return 'Excluded by budget';
      default: return this.kindLabel(source.status);
    }
  }

  statusTone(source: OrchestratorContextSourceReceipt): StatusTone {
    return contextSourceStatusTone(source.status);
  }

  shortRevision(revision: string | null | undefined): string | null {
    if (!revision) return null;
    return revision.length > 12 ? revision.slice(0, 12) : revision;
  }
}
