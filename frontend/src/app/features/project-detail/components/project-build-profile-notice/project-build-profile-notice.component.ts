import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';

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
  /** AGT-3428: whether the repository defines its verify commands, with a minimal example when it does not. */
  verifyDefinition?: {
    state: 'derived' | 'declared-empty' | 'missing';
    path: string;
    message: string | null;
    exampleStack: string | null;
    exampleDefinition: string | null;
    examplePrepareScript: string | null;
  };
}

@Component({
  selector: 'app-project-build-profile-notice',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './project-build-profile-notice.component.html',
  styleUrl: './project-build-profile-notice.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectBuildProfileNoticeComponent {
  readonly summary = input.required<BuildProfileGateSummary>();
}
