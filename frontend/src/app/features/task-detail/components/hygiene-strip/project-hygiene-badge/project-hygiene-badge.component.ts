import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input } from '@angular/core';
import { GitHygieneService } from '../../../../../services/git-hygiene.service';

import { StatusIndicatorComponent, type StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

/**
 * Project repo hygiene as a shared status indicator (AGT-3403): a dirty tree
 * needs an operator step (warning), unpushed commits are a fact worth noticing
 * (info), clean and in sync renders nothing.
 */
export function projectHygieneIndicator(isDirty: boolean, ahead: number): { tone: StatusTone; label: string } | null {
  if (isDirty) return { tone: 'warning', label: 'dirty' };
  if (ahead > 0) return { tone: 'info', label: `${ahead} unpushed` };
  return null;
}
/**
 * Compact dirty / unpushed badge rendered on the project header chip.
 * Subscribes to the shared GitHygieneService so multiple consumers share
 * one polling loop per project. Renders nothing when the project is clean
 * and in sync (no upstream is treated as "no signal" - we don't shout
 * about repos the user hasn't wired to a remote yet).
 */
@Component({
  selector: 'app-project-hygiene-badge',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-hygiene-badge.component.html',
  styleUrl: './project-hygiene-badge.component.scss'
})
export class ProjectHygieneBadgeComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  private readonly hygieneSvc = inject(GitHygieneService);
  private stopFn: (() => void) | null = null;

  private readonly status = computed(() => this.hygieneSvc.forProject(this.projectName())());

  readonly isDirty = computed(() => !!this.status()?.isDirty);
  readonly isAhead = computed(() => {
    const s = this.status();
    return !!s && s.hasUpstream && s.ahead > 0;
  });
  readonly aheadCount = computed(() => this.status()?.ahead ?? 0);
  readonly visible = computed(() => this.isDirty() || this.isAhead());
  readonly indicator = computed(() =>
    projectHygieneIndicator(this.isDirty(), this.isAhead() ? this.aheadCount() : 0));

  readonly tooltip = computed(() => {
    const s = this.status();
    if (!s) return '';
    const parts: string[] = [];
    if (s.isDirty) {
      parts.push(`Working tree dirty: ${s.stagedCount} staged, ${s.unstagedCount} unstaged, ${s.untrackedCount} untracked.`);
    }
    if (s.hasUpstream && s.ahead > 0) {
      parts.push(`${s.ahead} commit${s.ahead === 1 ? '' : 's'} ahead of ${s.upstream}.`);
    }
    return parts.join(' ');
  });

  ngOnInit(): void {
    this.stopFn = this.hygieneSvc.ensurePolling(this.projectName());
  }

  ngOnDestroy(): void {
    this.stopFn?.();
    this.stopFn = null;
  }
}
