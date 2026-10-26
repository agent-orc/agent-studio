import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { formatAccessTime, type EnrolledRunner, type RunnerEnrollment } from '../../models/access.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessService } from '../../services/access.service';

/**
 * Runner enrollment codes and enrolled runners (AGT-3186). Runners and the
 * engine authenticate with their enrollment credentials and Runner scopes,
 * not with role bindings.
 */
@Component({
  selector: 'app-access-runners',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-runners.component.html',
  styleUrl: './access-runners.component.scss',
})
export class AccessRunnersComponent {
  readonly access = inject(AccessService);
  private readonly now = inject(ACCESS_CLOCK);
  readonly name = signal('');
  readonly kind = signal<'runner' | 'orchestrator'>('runner');
  readonly code = signal<string | null>(null);
  readonly time = formatAccessTime;

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.access.busy()) return;
    const created = await this.access.createEnrollment(name, this.kind());
    if (created) {
      this.code.set(created.enrollmentCode);
      this.name.set('');
    }
  }

  setKind(value: string): void {
    this.kind.set(value === 'orchestrator' ? 'orchestrator' : 'runner');
  }

  status(enrollment: RunnerEnrollment): 'Used' | 'Open' | 'Expired or revoked' {
    if (enrollment.usedAt) return 'Used';
    return this.open(enrollment) ? 'Open' : 'Expired or revoked';
  }

  open(enrollment: RunnerEnrollment): boolean {
    return !enrollment.usedAt && Date.parse(enrollment.expiresAt) > this.now();
  }

  activeCredentials(runner: EnrolledRunner): number {
    return runner.credentials.filter(credential => !credential.revokedAt).length;
  }

  async revokeEnrollment(enrollment: RunnerEnrollment): Promise<void> {
    if (!this.access.busy()) await this.access.revokeEnrollment(enrollment.id);
  }

  async revokeRunner(runner: EnrolledRunner): Promise<void> {
    if (!this.access.busy()) await this.access.revokeRunner(runner.id);
  }
}
