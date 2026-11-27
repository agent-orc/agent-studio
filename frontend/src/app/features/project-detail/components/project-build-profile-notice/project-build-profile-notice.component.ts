import { ChangeDetectionStrategy, Component, DOCUMENT, computed, inject, input } from '@angular/core';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { ServerCheckoutStore } from '../../../server-checkout';

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
    state: 'derived' | 'declared-empty' | 'missing' | 'checkout-missing';
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
  readonly projectName = input<string | null>(null);
  private readonly document = inject(DOCUMENT);
  private readonly serverCheckout = inject(ServerCheckoutStore);

  /**
   * AGT-3933: while Project basics offers Create server checkout, that notice is
   * the one remedy for a URL project without a checkout. The path field only
   * accepts an existing clone, so the checkout-missing variant stays hidden.
   */
  readonly serverCheckoutOffered = computed(() => this.serverCheckout.viewFor(
    this.serverCheckout.projectFor({ name: this.projectName() })) !== null);

  jumpToCheckout(event: Event): void {
    event.preventDefault();
    const target = this.document.getElementById('project-basics-repository-path');
    if (!target) return;
    target.scrollIntoView?.({ block: 'center' });
    target.focus({ preventScroll: true });
  }
}
