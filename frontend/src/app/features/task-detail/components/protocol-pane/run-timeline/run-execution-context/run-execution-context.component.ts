import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent } from '../../../../../../components/status-indicator/status-indicator.component';
import type { CliContextSource, CliExecutionContext, RunRecord } from '../../../../../../features/run-timeline';

@Component({
  selector: 'app-run-execution-context',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-execution-context.component.html',
  styleUrl: './run-execution-context.component.scss',
})
export class RunExecutionContextComponent {
  readonly run = input.required<RunRecord>();

  hasExecutionContext(): boolean {
    const context = this.run().executionContext;
    return !!context && (
      context.sources.length > 0 ||
      !!context.model ||
      !!context.thinkingLevel ||
      !!context.permissionMode ||
      !!context.cwd
    );
  }

  executionGroups(): { kind: string; label: string; sources: CliContextSource[] }[] {
    const context = this.run().executionContext;
    if (!context || context.sources.length === 0) return [];
    const order = ['memory', 'instruction-file', 'mcp', 'session', 'global-config', 'env'];
    const byKind = new Map<string, CliContextSource[]>();
    for (const source of context.sources) {
      const sources = byKind.get(source.kind) ?? [];
      sources.push(source);
      byKind.set(source.kind, sources);
    }
    return [...byKind.keys()]
      .sort((left, right) => {
        const leftIndex = order.indexOf(left);
        const rightIndex = order.indexOf(right);
        return (leftIndex < 0 ? order.length : leftIndex) -
          (rightIndex < 0 ? order.length : rightIndex);
      })
      .map(kind => ({ kind, label: this.kindLabel(kind), sources: byKind.get(kind)! }));
  }

  executionSourceLabel(context: CliExecutionContext | null | undefined): string {
    return context?.source === 'init-frame'
      ? 'reported by CLI init frame'
      : 'derived from config conventions';
  }

  private kindLabel(kind: string): string {
    switch (kind) {
      case 'memory': return 'Memory';
      case 'instruction-file': return 'Instruction files';
      case 'session': return 'Session store';
      case 'global-config': return 'Global config';
      case 'mcp': return 'MCP servers';
      case 'env': return 'Environment';
      default: return kind || 'Other';
    }
  }
}
