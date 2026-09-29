import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type {
  TokenTimelineFreshness,
  TokenTimelineModelUsage,
  TokenTimelineProject,
} from '../../models/tokens.model';
import { formatCompactTokens, formatCompactUsd } from '../../token-number-format.util';

/**
 * Executing-host breakdown under the workspace token timeline (AGT-2986).
 * Remote runner usage and local workstation usage come from one merged
 * ledger, so each enabled project lists its hosts and the table shows one
 * row per (project, model id, host). The model label is resolved by the
 * backend from the registry; an unknown id arrives as the id itself.
 */
@Component({
  selector: 'app-workspace-token-model-table',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-token-model-table.component.html',
  styleUrl: './workspace-token-model-table.component.scss',
})
export class WorkspaceTokenModelTableComponent {
  readonly models = input<readonly TokenTimelineModelUsage[]>([]);
  readonly projects = input<readonly TokenTimelineProject[]>([]);
  readonly hiddenProjects = input<ReadonlySet<string>>(new Set<string>());
  readonly freshness = input<TokenTimelineFreshness | undefined>(undefined);
  readonly colorFor = input<(project: string) => string>(() => 'transparent');

  readonly formatTokens = formatCompactTokens;
  readonly formatUsd = formatCompactUsd;

  readonly visibleModels = computed(() => {
    const off = this.hiddenProjects();
    return this.models().filter(m => !off.has(m.project));
  });

  readonly visibleProjects = computed(() => {
    const off = this.hiddenProjects();
    return this.projects().filter(p => !off.has(p.project) && (p.hosts?.length ?? 0) > 0);
  });

  // Footer: the sum of the visible rows, which equals the project table
  // total for the same enabled projects.
  readonly totals = computed(() => {
    let total = 0, calls = 0, dollars = 0;
    let anyDollars = false, allPriced = true;
    for (const m of this.visibleModels()) {
      total += m.total;
      calls += m.calls;
      if (m.dollars !== null) {
        dollars += m.dollars;
        anyDollars = true;
        if (!m.allModelsPriced) allPriced = false;
      } else {
        allPriced = false;
      }
    }
    return { total, calls, dollars: anyDollars ? dollars : null, allPriced };
  });

  readonly warning = computed<string | null>(() => {
    const f = this.freshness();
    if (!f || f.status === 'complete') return null;
    return f.warning ?? 'Some usage sources could not be read. Values may be incomplete.';
  });

  modelKey(m: TokenTimelineModelUsage): string {
    return `${m.project}|${m.model}|${m.host}`;
  }
}
