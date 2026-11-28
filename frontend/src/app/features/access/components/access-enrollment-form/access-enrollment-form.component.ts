import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { CopyableCommandComponent } from '@studio/ui/components/copyable-command/copyable-command.component';
import { SecretRevealComponent } from '../../../../components/secret-reveal/secret-reveal.component';
import { ALL_RUNNER_PROJECTS, REPOSITORY_ACCESS_LABELS, runHostCommand, type CreatedEnrollment, type EnrollmentKind } from '../../models/access.model';
import { toggleProject } from '../../models/access-view.model';
import { AccessService } from '../../services/access.service';

/**
 * Creates a one-time enrollment code (AGT-3186). A runner or run host code
 * names its projects (SM-9); the engine is not project-bound. The code is
 * shown once; a run host code also shows the commands that enroll the host
 * and start its deploy service (F4).
 */
@Component({
  selector: 'app-access-enrollment-form',
  standalone: true,
  imports: [CopyableCommandComponent, SecretRevealComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-enrollment-form.component.html',
  styleUrl: './access-enrollment-form.component.scss',
})
export class AccessEnrollmentFormComponent {
  readonly access = inject(AccessService);
  readonly name = signal('');
  readonly kind = signal<EnrollmentKind | null>(null);
  readonly issued = signal<CreatedEnrollment | null>(null);
  readonly repositoryLabels = REPOSITORY_ACCESS_LABELS;
  /** Set with a run host code: the host commands that enroll it and start its deploy service (F4). */
  readonly hostCommand = signal<string | null>(null);
  /** Projects of the next enrollment code; none chosen binds none. */
  readonly selected = signal<readonly string[]>([]);
  readonly allProjects = ALL_RUNNER_PROJECTS;

  constructor() {
    effect(() => {
      if (this.access.state() === 'denied') this.done();
    });
  }

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.access.busy()) return;
    const kind = this.kind();
    if (!kind) return;
    const created = kind === 'orchestrator'
      ? await this.access.createEnrollment(name, 'orchestrator')
      : await this.access.createEnrollment(name, kind, this.selected());
    if (created && this.access.state() !== 'denied') {
      this.issued.set(created);
      this.hostCommand.set(kind === 'deploy' ? runHostCommand(location.origin, created.enrollmentCode) : null);
      this.name.set('');
      this.selected.set([]);
    }
  }

  done(): void {
    this.issued.set(null);
    this.hostCommand.set(null);
  }

  setKind(value: string): void {
    this.kind.set(value === 'coding' || value === 'review' || value === 'orchestrator' || value === 'deploy' ? value : null);
  }

  toggle(project: string): void {
    this.selected.set(toggleProject(this.selected(), project));
  }
}
