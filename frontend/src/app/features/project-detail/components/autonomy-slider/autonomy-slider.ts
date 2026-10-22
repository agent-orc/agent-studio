import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';

import { TooltipDirective } from 'coding-agent-chat/shared';
export interface AutonomyStop {
  level: number;
  name: string;
  desc: string;
}

/** ADR-0026 autonomy stops; Project Settings shows the same labels. */
export const AUTONOMY_STOPS: readonly AutonomyStop[] = [
  { level: 0, name: 'Manual', desc: 'Orchestrator never moves a task forward without a human click. Queue may be empty.' },
  { level: 1, name: 'Cautious', desc: 'Orchestrator may sharpen prompts once. Every borderline task is bounced to NeedsClar for clarification.' },
  { level: 2, name: 'Balanced', desc: 'Default. Orchestrator iterates up to 3 times and decides on clear cases. Bounces only the genuinely-unclear.' },
  { level: 3, name: 'Confident', desc: 'Orchestrator decides on most cases. Cap-exit raises a supervisor advisory instead of bouncing.' },
  { level: 4, name: 'Fully auto', desc: 'Never bounces. Unclear tasks ship to Ready with a [supervisor] chat-note. Queue is never allowed to stall on ambiguity.' },
];

/**
 * ADR-0026: per-project orchestrator-prep autonomy slider. Persists to
 * `/api/projects/{name}/autonomy` on every change. The next pickup tick
 * honours the new value; in-flight prep iterations finish under the old
 * value (no mid-iteration policy switch).
 */
@Component({
  selector: 'app-autonomy-slider',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './autonomy-slider.html',
  styleUrl: './autonomy-slider.scss'
})
export class AutonomySliderComponent implements OnInit {
  readonly projectName = input.required<string>();
  readonly stops = AUTONOMY_STOPS;

  private readonly jobService = inject(TaskService);

  readonly level = signal<number>(2);

  readonly currentStop = computed(() => AUTONOMY_STOPS[this.level()] ?? AUTONOMY_STOPS[2]);

  ngOnInit() {
    this.jobService.getProjectAutonomyLevel(this.projectName()).subscribe({
      next: (resp) => this.level.set(this.clamp(resp?.level ?? 2)),
      error: () => { /* keep default; backend may be older */ }
    });
  }

  onChange(value: number) {
    const clamped = this.clamp(Number(value));
    if (clamped === this.level()) return;
    this.level.set(clamped);
    this.jobService.setProjectAutonomyLevel(this.projectName(), clamped).subscribe({
      error: () => { /* roll back not strictly needed; the next read will reconcile */ }
    });
  }

  private clamp(n: number) {
    if (Number.isNaN(n)) return 2;
    return Math.max(0, Math.min(4, Math.round(n)));
  }
}
