import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { BuildProfileContradiction } from '../project-build-profile-notice/project-build-profile-notice.component';

const FIELD_LABELS: Record<string, string> = {
  build: 'build',
  test: 'test',
};

/**
 * AGT-2827: `.agent-studio/project.yml` always wins outright over a central
 * BuildProfile, so a profile that still declares different commands is
 * silently ignored. Warns the operator in project settings instead of
 * leaving the mismatch to surface only as a confusing review failure.
 */
@Component({
  selector: 'app-project-build-profile-contradiction-notice',
  standalone: true,
  templateUrl: './project-build-profile-contradiction-notice.component.html',
  styleUrl: './project-build-profile-contradiction-notice.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectBuildProfileContradictionNoticeComponent {
  readonly contradictions = input.required<readonly BuildProfileContradiction[]>();

  readonly rows = computed(() =>
    this.contradictions().map((c) => ({
      ...c,
      label: FIELD_LABELS[c.field] ?? c.field,
    })),
  );
}
