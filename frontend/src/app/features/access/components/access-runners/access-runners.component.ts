import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  ALL_RUNNER_PROJECTS,
  formatAccessTime,
  type EnrolledRunner,
  type RunnerEnrollment,
} from '../../models/access.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessService } from '../../services/access.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** A credential within this many days of its end is shown as expiring. */
const EXPIRY_WARNING_DAYS = 14;
const DAY_MS = 24 * 60 * 60 * 1000;

export interface RunnerExpiry {
  label: string;
  tone: 'attention' | 'muted' | null;
  notice: string | null;
}

/**
 * Runner enrollment codes and enrolled runners (AGT-3186). Runners and the
 * engine authenticate with their enrollment credentials and Runner scopes,
 * not with role bindings. SM-9 binds every runner to an explicit project set
 * and gives each new credential a 30-day lifetime.
 */
@Component({
  selector: 'app-access-runners',
  standalone: true,
  imports: [StatusIndicatorComponent],
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
  /** Projects of the next enrollment code; none chosen binds none. */
  readonly selected = signal<readonly string[]>([]);
  /** The runner whose projects are being edited, and the draft of its project set. */
  readonly editing = signal<string | null>(null);
  readonly draft = signal<readonly string[]>([]);
  readonly allProjects = ALL_RUNNER_PROJECTS;
  readonly time = formatAccessTime;

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.access.busy()) return;
    const created = this.kind() === 'orchestrator'
      ? await this.access.createEnrollment(name, 'orchestrator')
      : await this.access.createEnrollment(name, 'runner', this.selected());
    if (created) {
      this.code.set(created.enrollmentCode);
      this.name.set('');
      this.selected.set([]);
    }
  }

  setKind(value: string): void {
    this.kind.set(value === 'orchestrator' ? 'orchestrator' : 'runner');
  }

  /** Enrollment state on the shared indicator: an open code waits, a used one succeeded. */
  statusTone(enrollment: RunnerEnrollment): StatusTone {
    const status = this.status(enrollment);
    return status === 'Used' ? 'success' : status === 'Open' ? 'info' : 'neutral';
  }

  toggleSelected(project: string): void {
    this.selected.set(toggle(this.selected(), project));
  }

  toggleDraft(project: string): void {
    this.draft.set(toggle(this.draft(), project));
  }

  edit(runner: EnrolledRunner): void {
    this.editing.set(runner.id);
    this.draft.set(runner.projectsNotice ? [] : [...(runner.projects ?? [])]);
  }

  cancelEdit(): void {
    this.editing.set(null);
  }

  async saveProjects(runner: EnrolledRunner): Promise<void> {
    if (this.access.busy()) return;
    const saved = await this.access.updateRunnerProjects(runner.id, this.draft());
    if (saved !== null) this.editing.set(null);
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

  /** The engine authenticates with an orchestration credential and is not bound to projects. */
  isEngine(runner: EnrolledRunner): boolean {
    return runner.id.startsWith('engine_');
  }

  projectNames(projects: readonly string[] | null | undefined): string {
    const list = projects ?? [];
    if (list.includes(ALL_RUNNER_PROJECTS)) return 'All projects';
    if (list.length === 0) return 'No project';
    const names = new Map(this.access.projects().map(project => [project.id.toLowerCase(), project.displayName]));
    return list.map(project => names.get(project.toLowerCase()) ?? project).join(', ');
  }

  /** The soonest end among the runner's active credentials, and why one has none. */
  expiry(runner: EnrolledRunner): RunnerExpiry {
    const active = runner.credentials.filter(credential => !credential.revokedAt);
    const unexpiring = active.find(credential => !credential.expiresAt);
    if (unexpiring)
      return { label: 'No expiry', tone: 'muted', notice: unexpiring.expiryNotice ?? null };
    const ends = active.map(credential => Date.parse(credential.expiresAt!)).filter(value => !Number.isNaN(value));
    if (ends.length === 0) return { label: '', tone: null, notice: null };
    const soonest = Math.min(...ends);
    const days = Math.ceil((soonest - this.now()) / DAY_MS);
    if (days <= 0) return { label: 'Expired', tone: 'attention', notice: null };
    if (days <= EXPIRY_WARNING_DAYS)
      return { label: days === 1 ? 'Expires in 1 day' : `Expires in ${days} days`, tone: 'attention', notice: null };
    return { label: `Expires ${formatAccessTime(new Date(soonest).toISOString())}`, tone: null, notice: null };
  }

  async revokeEnrollment(enrollment: RunnerEnrollment): Promise<void> {
    if (!this.access.busy()) await this.access.revokeEnrollment(enrollment.id);
  }

  async revokeRunner(runner: EnrolledRunner): Promise<void> {
    if (!this.access.busy()) await this.access.revokeRunner(runner.id);
  }
}

/** Choosing every project replaces single choices, and a single choice leaves "every project". */
function toggle(current: readonly string[], project: string): readonly string[] {
  if (current.includes(project)) return current.filter(item => item !== project);
  if (project === ALL_RUNNER_PROJECTS) return [ALL_RUNNER_PROJECTS];
  return [...current.filter(item => item !== ALL_RUNNER_PROJECTS), project];
}
