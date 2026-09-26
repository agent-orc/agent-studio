import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { OrchestratorChatTurn } from '../../models/orchestrator.model';

@Component({
  selector: 'app-chat-usage-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-usage-header.component.html',
  styleUrl: './chat-usage-header.component.scss',
})
export class ChatUsageHeaderComponent {
  readonly turns = input.required<readonly OrchestratorChatTurn[]>();
  readonly enabled = input.required<boolean>();
  readonly toggleRequested = output<void>();

  readonly totals = computed(() => {
    const replies = this.turns().filter(turn => turn.role === 'orchestrator' && turn.metadata);
    return {
      turns: replies.length,
      tokens: replies.reduce((sum, turn) => sum + (turn.tokenUsage
        ? turn.tokenUsage.inputTokens + turn.tokenUsage.outputTokens
          + turn.tokenUsage.cacheReadTokens + turn.tokenUsage.cacheCreationTokens : 0), 0),
      cost: replies.reduce((sum, turn) => sum + (turn.metadata?.cost ?? 0), 0),
      priced: replies.filter(turn => turn.metadata?.cost != null).length,
      withoutUsage: replies.filter(turn => !turn.tokenUsage).length,
      durationMs: replies.reduce((sum, turn) => sum + (turn.metadata?.totalLatencyMs ?? 0), 0),
      models: [...new Set(replies.map(turn => turn.metadata?.model).filter((value): value is string => !!value))],
      sessionIds: [...new Set(replies.map(turn => turn.metadata?.providerSessionId).filter((value): value is string => !!value))],
    };
  });
}
