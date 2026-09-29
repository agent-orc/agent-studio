import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import type { OrchestratorChatTurn } from '../../models/orchestrator.model';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';

@Component({
  selector: 'app-orchestrator-chat-usage-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-chat-usage-header.component.html',
  styleUrl: './orchestrator-chat-usage-header.component.scss',
})
export class OrchestratorChatUsageHeaderComponent {
  readonly turns = input.required<readonly OrchestratorChatTurn[]>();
  readonly project = input<string | null>(null);
  readonly preferences = inject(UiPreferencesService);
  private readonly http = inject(HttpClient);
  readonly summary = computed(() => {
    const turns = this.turns().filter(turn => turn.role === 'orchestrator' && turn.metadata);
    const tokens = turns.reduce((sum, turn) => sum + (turn.metadata?.inputTokens ?? 0)
      + (turn.metadata?.cachedInputTokens ?? 0) + (turn.metadata?.outputTokens ?? 0), 0);
    const cost = turns.reduce((sum, turn) => sum + (turn.metadata?.cost ?? 0), 0);
    const costText = turns.some(turn => turn.metadata?.cost == null)
      ? 'cost incomplete' : `$${cost.toFixed(4)}`;
    return `${turns.length} ${turns.length === 1 ? 'turn' : 'turns'} · ${tokens.toLocaleString('en-US')} tokens · ${costText}`;
  });
  readonly details = computed(() => {
    const turns = this.turns().filter(turn => turn.role === 'orchestrator' && turn.metadata);
    const models = [...new Set(turns.map(turn => turn.metadata?.model).filter(Boolean))];
    const latest = turns.at(-1)?.metadata;
    const elapsed = turns.reduce((sum, turn) => sum + milliseconds(turn.metadata?.queuedAt, turn.metadata?.finishedAt), 0);
    const waiting = turns.reduce((sum, turn) => sum + milliseconds(turn.metadata?.queuedAt, turn.metadata?.startedAt), 0);
    return [models.length === 1 ? models[0] : models.length > 1 ? `${models.length} models` : null,
      elapsed > 0 ? `${Math.round(elapsed / 1000)}s total (${Math.round(waiting / 1000)}s waiting)` : null,
      latest?.host,
      latest?.providerThreadId ? `session ${latest.providerThreadId}` : null,
    ].filter(Boolean).join(' · ');
  });

  constructor() {
    effect((onCleanup) => {
      const project = this.project();
      // A project switch must never inherit the previous project's value, so the
      // default-on setting applies until this project's own default arrives.
      const projectDefault = this.preferences.chatMetadataProjectDefault;
      projectDefault.set(true);
      if (!project) return;
      const subscription = this.http.get<{ chatMetadataEnabled?: unknown } | null>(
        `/api/projects/${encodeURIComponent(project)}/chat-metadata`,
      ).subscribe({
        next: response => projectDefault.set(
          typeof response?.chatMetadataEnabled === 'boolean' ? response.chatMetadataEnabled : true),
        error: () => projectDefault.set(true),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  toggle(): void {
    this.preferences.setChatMetadataOverride(!this.preferences.chatMetadataEnabled());
  }
}

function milliseconds(start?: string | null, finish?: string | null): number {
  if (!start || !finish) return 0;
  const duration = Date.parse(finish) - Date.parse(start);
  return Number.isFinite(duration) && duration > 0 ? duration : 0;
}
