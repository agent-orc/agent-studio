import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import type { OrchestratorLogEntry } from '../../models/orchestrator.model';

/** One width-stable row in the workspace chronological event stream. */
@Component({
  selector: 'app-orchestrator-feed-entry',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed-entry.html',
  styleUrl: './orchestrator-feed-entry.scss',
})
export class OrchestratorFeedEntryComponent {
  private readonly projectLookup = inject(ProjectLookupService);

  readonly entry = input.required<OrchestratorLogEntry>();
  readonly projectFallback = input('Workspace');
  readonly selected = input(false);
  readonly selectRequest = output<OrchestratorLogEntry>();
  readonly projectFilterRequest = output<string>();
  readonly openTaskRequest = output<OrchestratorLogEntry>();

  select(): void {
    this.selectRequest.emit(this.entry());
  }

  filterProject(event: MouseEvent, project: string): void {
    event.stopPropagation();
    this.projectFilterRequest.emit(project);
  }

  openTask(event: MouseEvent): void {
    event.stopPropagation();
    this.openTaskRequest.emit(this.entry());
  }

  kindLabel(kind: string): string {
    return kind === 'alert' ? 'Alert'
      : kind === 'decision' ? 'Decision'
      : kind === 'action' ? 'Action'
      : kind === 'observation' ? 'Observation'
      : kind === 'intervention' ? 'Intervention'
      : kind;
  }

  formatTime(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('en-US');
  }

  formatClock(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime())
      ? iso
      : date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
  }

  projectColor(project: string): string {
    return this.projectLookup.getProjectDisplay(project).color;
  }

  projectCode(project: string): string {
    const display = this.projectLookup.getProjectDisplay(project);
    if (display.shortCode) return display.shortCode;
    const words = display.displayName.match(/[A-Za-z0-9]+/g) ?? [];
    return words.length > 1
      ? words.slice(0, 4).map(word => word[0]).join('').toUpperCase()
      : (words[0] ?? '?').slice(0, 3).toUpperCase();
  }

}
