import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TokenTimelineProject } from '../../models/tokens.model';
import { formatCompactTokens } from '../../token-number-format.util';

@Component({
  selector: 'app-workspace-token-composition',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-token-composition.component.html',
  styleUrl: './workspace-token-composition.component.scss',
})
export class WorkspaceTokenCompositionComponent {
  readonly projects = input.required<readonly TokenTimelineProject[]>();
  readonly disabled = input.required<ReadonlySet<string>>();
  readonly total = input.required<number>();
  readonly categories = [
    { key: 'agent', label: 'Agent', cls: 'cat--agent' },
    { key: 'supporting', label: 'Supporting', cls: 'cat--supporting' },
    { key: 'orchestrator', label: 'Orchestrator', cls: 'cat--orchestrator' },
    { key: 'chat', label: 'Chat', cls: 'cat--chat' },
  ] as const;
  readonly amounts = computed(() => {
    const result = { agent: 0, supporting: 0, orchestrator: 0, chat: 0 };
    for (const project of this.projects()) {
      if (this.disabled().has(project.project)) continue;
      result.agent += project.agentTokens;
      result.supporting += project.supportingTokens;
      result.orchestrator += project.orchestratorTokens;
      result.chat += project.chatTokens ?? 0;
    }
    return result;
  });
  formatTokens = formatCompactTokens;
  pct(amount: number): number { return this.total() > 0 ? amount / this.total() * 100 : 0; }
}
