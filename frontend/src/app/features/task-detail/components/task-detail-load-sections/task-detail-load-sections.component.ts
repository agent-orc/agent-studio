import { ChangeDetectionStrategy, Component, ElementRef, HostListener, afterRenderEffect, inject, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { laneLabelFor } from '../../state/triage-actions.model';
import type { TaskCore, ResourceName, ResourcePhase } from '../../state/task-core.model';
import { resourceReasonLabel } from '../../state/task-core.model';
import { LanePagerService } from '../../state/lane-pager.service';
import { taskDetailShortcutTargetAllowed, taskNavigationOwnsFocus } from '../../task-detail-keyboard.util';

interface LoadingSection {
  id: 'context' | 'activity' | 'evidence';
  label: string;
}

@Component({
  selector: 'app-task-detail-load-sections',
  standalone: true,
  templateUrl: './task-detail-load-sections.component.html',
  styleUrl: './task-detail-load-sections.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskDetailLoadSectionsComponent {
  private readonly element = inject(ElementRef<HTMLElement>);
  readonly pager = inject(LanePagerService);
  readonly info = input.required<TaskInfo>();
  readonly core = input<TaskCore | null>(null);
  readonly resources = input<Record<ResourceName, { phase: ResourcePhase; reason: string | null }> | null>(null);
  readonly errorMessage = input<string | null>(null);
  readonly back = output<void>();
  readonly retry = output<void>();
  readonly next = output<void>();
  readonly prev = output<void>();
  readonly laneLabel = laneLabelFor;
  readonly reasonLabel = resourceReasonLabel;
  readonly sections: readonly LoadingSection[] = [
    { id: 'context', label: 'Task context' },
    { id: 'activity', label: 'Activity and result' },
    { id: 'evidence', label: 'Git evidence' },
  ];

  constructor() {
    afterRenderEffect(() => {
      const core = this.core();
      if (!core || core.state === 'warming') return;
      const host = this.element.nativeElement;
      if (host.querySelector('[data-testid="task-core-identity"]')
        && host.querySelector('[data-testid="task-core-state"]')
        && host.querySelector('[data-testid="task-core-pins"]')
        && host.querySelector('[data-testid="task-core-execution"]')
        && host.querySelector('[data-testid="task-core-status"]')
        && host.querySelector('[data-testid="task-core-prompt"]')
        && host.querySelector('[data-testid="task-core-timeline"]')) {
        requestAnimationFrame(() => setTimeout(() => {
          if (this.core() === core) performance.mark('task-core-ready');
        }, 0));
      }
    });
  }

  @HostListener('document:keydown', ['$event'])
  onNavigationKey(event: KeyboardEvent): void {
    if (!this.core() || !taskDetailShortcutTargetAllowed(event)) return;
    const arrow = event.key.startsWith('Arrow');
    if (arrow && !taskNavigationOwnsFocus(event)) return;
    if (event.key === 'j' || event.key === 'ArrowDown' || event.key === 'ArrowRight') {
      event.preventDefault(); this.next.emit();
    } else if (event.key === 'k' || event.key === 'ArrowUp' || event.key === 'ArrowLeft') {
      event.preventDefault(); this.prev.emit();
    }
  }
}
