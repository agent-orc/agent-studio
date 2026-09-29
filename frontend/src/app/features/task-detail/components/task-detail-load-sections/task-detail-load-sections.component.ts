import { ChangeDetectionStrategy, Component, ElementRef, HostListener, afterRenderEffect, inject, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { laneLabelFor } from '../../state/triage-actions.model';
import type { TaskCore } from '../../../../models/task-core.model';
import { resourceReasonLabel, type ResourceStates } from '../../state/task-resource-states';
import { LanePagerService } from '../../state/lane-pager.service';
import { taskDetailShortcutTargetAllowed, taskNavigationOwnsFocus } from '../../task-detail-keyboard.util';

const CORE_SECTIONS = ['identity', 'state', 'pins', 'execution', 'status', 'prompt', 'timeline'] as const;

/** True when `host` shows every required core section for `core`'s generation. */
export function corePainted(host: HTMLElement, core: TaskCore): boolean {
  const root = host.querySelector<HTMLElement>('[data-testid="task-core"]');
  if (root?.dataset['coreId'] !== core.id || root.dataset['coreVersion'] !== core.coreVersion)
    return false;
  return CORE_SECTIONS.every(id => {
    const section = host.querySelector(`[data-testid="task-core-${id}"]`);
    if (!section) return false;
    const heading = section.querySelector('h2')?.textContent ?? '';
    return (section.textContent ?? '').replace(heading, '').trim().length > 0;
  });
}

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
  readonly resources = input<ResourceStates | null>(null);
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
    // `task-core-ready` is the core-ready contract: identity, state, pins,
    // execution, status summary, prompt head and timeline head (or their
    // explicit empty states) are painted for this exact core generation.
    // A mark therefore needs the DOM to carry the core's id and generation
    // and every required section to show content beyond its heading.
    afterRenderEffect(() => {
      const core = this.core();
      if (!core || core.state === 'warming') return;
      if (!corePainted(this.element.nativeElement, core)) return;
      requestAnimationFrame(() => setTimeout(() => {
        if (this.core() === core) performance.mark('task-core-ready');
      }, 0));
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
