import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** One command list where the central BuildProfile disagrees with the repository's own `.agent-studio/project.yml`. */
export interface BuildProfileContradiction {
  field: string;
  profileCommands: readonly string[];
  repositoryCommands: readonly string[];
}

export interface BuildProfileGateSummary {
  profile: unknown | null;
  gateApplicable: boolean;
  verifyPlan: {
    source: string;
    commands: readonly unknown[];
  };
  contradictions?: readonly BuildProfileContradiction[];
}

@Component({
  selector: 'app-project-build-profile-notice',
  standalone: true,
  templateUrl: './project-build-profile-notice.component.html',
  styleUrl: './project-build-profile-notice.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectBuildProfileNoticeComponent {
  readonly summary = input.required<BuildProfileGateSummary>();
}
