import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { RowComponent } from '@studio/ui/components/row/row.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { ALL_RUNNER_PROJECTS, formatAccessTime } from '../../models/access.model';
import {
  enrollmentIdentity, enrollmentOpen, enrollmentStatus, enrollmentTone, identityLabel, projectNames, runnerExpiry, runnerIdentity, toggleProject,
} from '../../models/access-view.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';

/**
 * Detail of one enrolled runner or one enrollment code: identity, bound
 * projects, credential, and revocation, set apart and confirmed in place.
 * SM-9: a runner's projects are edited here.
 */
@Component({
  selector: 'app-access-runner-detail',
  standalone: true,
  imports: [InlineConfirmComponent, PageSectionComponent, RowComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-runner-detail.component.html',
  styleUrl: './access-runner-detail.component.scss',
})
export class AccessRunnerDetailComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly now = inject(ACCESS_CLOCK);
  readonly kind = input.required<'runner' | 'enrollment'>();
  readonly itemId = input.required<string>();
  /** The draft of the runner's project set while it is being edited; null while it is not. */
  readonly draft = signal<readonly string[] | null>(null);
  readonly allProjects = ALL_RUNNER_PROJECTS;
  readonly time = formatAccessTime;

  readonly runner = computed(() => {
    if (this.kind() !== 'runner') return null;
    const runner = this.access.runners().find(candidate => candidate.id === this.itemId());
    if (!runner) return null;
    const identity = runnerIdentity(runner);
    return {
      runner, engine: identity === 'orchestrator', identity: identityLabel(identity),
      projects: projectNames(runner.projects, this.access.projects()),
      credentials: runner.credentials.filter(credential => !credential.revokedAt).length,
      expiry: runnerExpiry(runner, this.now()),
    };
  });

  readonly enrollment = computed(() => {
    if (this.kind() !== 'enrollment') return null;
    const enrollment = this.access.enrollments().find(candidate => candidate.id === this.itemId());
    if (!enrollment) return null;
    const identity = enrollmentIdentity(enrollment);
    const status = enrollmentStatus(enrollment, this.now());
    return {
      enrollment, status, tone: enrollmentTone(status), open: enrollmentOpen(enrollment, this.now()), identity: identityLabel(identity),
      projects: identity === 'orchestrator' ? 'Not project-bound' : enrollment.projects ? projectNames(enrollment.projects, this.access.projects()) : '',
    };
  });

  constructor() {
    effect(() => {
      this.itemId();
      untracked(() => this.draft.set(null));
    });
  }

  edit(): void {
    const runner = this.runner()?.runner;
    if (runner) this.draft.set(runner.projectsNotice ? [] : [...(runner.projects ?? [])]);
  }

  toggle(project: string): void {
    this.draft.set(toggleProject(this.draft() ?? [], project));
  }

  async saveProjects(): Promise<void> {
    const draft = this.draft();
    if (!draft || this.access.busy()) return;
    const saved = await this.access.updateRunnerProjects(this.itemId(), draft);
    if (saved !== null) this.draft.set(null);
  }

  async revoke(): Promise<void> {
    if (this.access.busy()) return;
    if (this.kind() === 'runner') await this.access.revokeRunner(this.itemId());
    else await this.access.revokeEnrollment(this.itemId());
  }
}
