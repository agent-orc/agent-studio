import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { CliCompletionContract } from '../../models/cli.model';
import type { CliType } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Completion detection shown per CLI: a typed adapter is the wanted state, exit-based needs attention. */
export function completionDetectionStatus(typed: boolean): { tone: StatusTone; label: string } {
  return typed ? { tone: 'success', label: 'typed adapter' } : { tone: 'warning', label: 'exit-based' };
}

/**
 * Completion-contract overview for the Admin/CLI page: how each CLI signals
 * turn completion. Data is the real backend registry served by
 * `GET /api/cli/contracts` (mirrored from the live `*EventAdapter` mappings),
 * so a CLI that has no typed adapter (Copilot) is shown honestly as
 * exit-based rather than papered over with a plausible-looking frame name.
 */
@Component({
  selector: 'app-cli-contracts-panel',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './cli-contracts-panel.html',
  styleUrl: './cli-contracts-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CliContractsPanelComponent implements OnInit {
  private readonly jobService = inject(TaskService);

  readonly contracts = signal<CliCompletionContract[]>([]);
  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly completionDetection = completionDetectionStatus;

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.errorMsg.set(null);
    this.jobService.getCliCompletionContracts().subscribe({
      next: (c) => {
        this.contracts.set(c ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.errorMsg.set('Failed to load completion contracts.');
        this.loading.set(false);
      },
    });
  }

  icon(t: CliType): string { return cliTypeIcon(t); }
  label(t: CliType): string { return cliTypeLabel(t); }
}
