import { afterRenderEffect, ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input, signal, untracked, viewChild } from '@angular/core';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/pending-button.directive';
import { SecretRevealComponent } from '../../../../components/secret-reveal/secret-reveal.component';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { RowComponent } from '@studio/ui/components/row/row.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import {
  ALL_RUNNER_PROJECTS, REPOSITORY_ACCESS_LABELS, formatAccessTime, repositoryAccess, withRepositoryAccess,
  type IssuedRunnerCredential, type RepositoryAccess,
} from '../../models/access.model';
import {
  enrollmentIdentity, enrollmentOpen, enrollmentStatus, enrollmentTone, identityLabel, projectNames, runnerCredentialActive, runnerExpiry, runnerIdentity, runnerRepositoryAccess, toggleProject,
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
  imports: [InlineConfirmComponent, PageSectionComponent, RowComponent, StatusIndicatorComponent, PendingButtonDirective, SecretRevealComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-runner-detail.component.html',
  styleUrl: './access-runner-detail.component.scss',
})
export class AccessRunnerDetailComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = inject(ACCESS_CLOCK);
  readonly kind = input.required<'runner' | 'enrollment'>();
  readonly itemId = input.required<string>();
  /** The draft of the runner's project set while it is being edited; null while it is not. */
  readonly draft = signal<readonly string[] | null>(null);
  readonly allProjects = ALL_RUNNER_PROJECTS;
  readonly time = formatAccessTime;
  readonly repositoryLabels = REPOSITORY_ACCESS_LABELS;
  readonly repositoryAccess = repositoryAccess;
  readonly credentialDraft = signal<RepositoryAccess | null>(null);
  readonly issuedCredential = signal<IssuedRunnerCredential | null>(null);
  readonly issuing = signal(false);
  private readonly credentialReveal = viewChild('credentialReveal', { read: ElementRef<HTMLElement> });

  private readonly denied = computed(() => this.access.state() === 'denied');

  readonly runner = computed(() => {
    if (this.kind() !== 'runner') return null;
    const runner = this.access.runners().find(candidate => candidate.id === this.itemId());
    if (!runner) return null;
    const identity = runnerIdentity(runner);
    const latestScopes = runner.credentials.filter(credential => !credential.revokedAt).at(-1)?.scopes;
    return {
      runner, engine: identity === 'orchestrator', identity: identityLabel(identity),
      projects: projectNames(runner.projects, this.access.projects()),
      credentials: runner.revokedAt ? [] : runner.credentials.filter(credential => runnerCredentialActive(credential, this.now())),
      repositoryAccess: runnerRepositoryAccess(runner, this.now()),
      latestScopes,
      // The repository credential route requires the runner's claim scope.
      canChooseRepositoryAccess: identity === 'runner' && (!latestScopes || latestScopes.includes('runner.claim')),
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
      repositoryAccess: repositoryAccess(enrollment.scopes),
      enrollment, status, tone: enrollmentTone(status), open: enrollmentOpen(enrollment, this.now()), identity: identityLabel(identity),
      projects: identity === 'orchestrator' ? 'Not project-bound' : enrollment.projects ? projectNames(enrollment.projects, this.access.projects()) : '',
    };
  });

  constructor() {
    effect(() => {
      this.itemId();
      this.denied();
      untracked(() => {
        this.draft.set(null);
        this.credentialDraft.set(null);
        this.issuedCredential.set(null);
      });
    });
    afterRenderEffect(() => this.credentialReveal()?.nativeElement.focus());
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

  editCredential(): void {
    const item = this.runner();
    if (item) this.credentialDraft.set(repositoryAccess(item.latestScopes ?? []));
  }

  async issueCredential(): Promise<void> {
    const item = this.runner();
    const draft = this.credentialDraft();
    if (!item || item.runner.revokedAt || draft === null || this.access.busy()) return;
    const scopes = item.canChooseRepositoryAccess ? withRepositoryAccess(draft, item.latestScopes) : undefined;
    this.issuing.set(true);
    try {
      const issued = await this.access.issueRunnerCredential(item.runner.id, scopes);
      if (issued && !this.destroyRef.destroyed && !this.denied() && this.itemId() === issued.runnerId) {
        this.credentialDraft.set(null);
        this.issuedCredential.set(issued);
      }
    } finally {
      this.issuing.set(false);
    }
  }

  async revoke(): Promise<void> {
    if (this.access.busy()) return;
    if (this.kind() === 'runner') await this.access.revokeRunner(this.itemId());
    else await this.access.revokeEnrollment(this.itemId());
  }
}
