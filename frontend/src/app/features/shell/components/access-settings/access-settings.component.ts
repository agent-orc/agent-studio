import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, forkJoin } from 'rxjs';
import { AuthService } from '../../../../services/auth.service';
import { AccessSettingsService, type AccessEnrollment, type AccessRunner, type AccessToken, type AccessUser } from './access-settings.service';

@Component({
  selector: 'app-access-settings',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-settings.component.html',
  styleUrl: './access-settings.component.scss',
})
export class AccessSettingsComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly api = inject(AccessSettingsService);
  readonly users = signal<AccessUser[]>([]);
  readonly tokens = signal<AccessToken[]>([]);
  readonly enrollments = signal<AccessEnrollment[]>([]);
  readonly runners = signal<AccessRunner[]>([]);
  readonly busy = signal(false);
  readonly accessDenied = signal(false);
  readonly error = signal<string | null>(null);
  readonly oneTime = signal<{ label: string; value: string } | null>(null);
  readonly inviteName = signal('');
  readonly inviteDisplayName = signal('');
  readonly inviteRole = signal('operator');
  readonly inviteProjects = signal('');
  readonly tokenName = signal('');
  readonly tokenRole = signal('operator');
  readonly tokenProjects = signal('');
  readonly tokenDays = signal(30);
  readonly runnerName = signal('');
  readonly runnerKind = signal<'runner' | 'orchestrator'>('runner');
  readonly editId = signal<string | null>(null);
  readonly editRole = signal('operator');
  readonly editProjects = signal('');

  ngOnInit(): void { if (this.isOwner()) this.refresh(); }
  isOwner(): boolean { return this.auth.status()?.user?.role === 'owner'; }

  refresh(): void {
    this.busy.set(true);
    forkJoin({ users: this.api.users(), tokens: this.api.tokens(), enrollments: this.api.enrollments(), runners: this.api.runners() })
      .subscribe({
        next: result => {
          this.users.set(result.users); this.tokens.set(result.tokens);
          this.enrollments.set(result.enrollments); this.runners.set(result.runners);
          this.accessDenied.set(false);
          this.busy.set(false); this.error.set(null);
        },
        error: err => this.failed(err),
      });
  }

  invite(): void {
    this.perform(this.api.invite(this.inviteName().trim(), this.inviteDisplayName().trim(),
      this.inviteRole(), this.projects(this.inviteProjects())), result => {
      const created = result as { temporaryPassword: string };
      this.oneTime.set({ label: 'Temporary password', value: created.temporaryPassword });
      this.inviteName.set(''); this.inviteDisplayName.set(''); this.inviteProjects.set('');
    });
  }
  edit(user: AccessUser): void {
    this.editId.set(user.id); this.editRole.set(user.role); this.editProjects.set(user.projects.join(', '));
  }
  saveUser(user: AccessUser): void {
    this.perform(this.api.updateUser({ ...user, role: this.editRole(), projects: this.projects(this.editProjects()) }),
      () => this.editId.set(null));
  }
  toggleUser(user: AccessUser): void {
    this.perform(this.api.updateUser({ ...user, disabled: !user.disabled }));
  }
  resetUser(user: AccessUser): void {
    this.perform(this.api.resetUser(user.id), result => {
      this.oneTime.set({ label: `Temporary password for ${user.username}`,
        value: (result as { temporaryPassword: string }).temporaryPassword });
    });
  }
  createToken(): void {
    const days = Math.min(365, Math.max(1, Number(this.tokenDays())));
    const expiresAt = new Date(Date.now() + days * 86400000).toISOString();
    this.perform(this.api.createToken(this.tokenName().trim(), this.tokenRole(),
      this.projects(this.tokenProjects()), expiresAt), result => {
      this.oneTime.set({ label: 'API token', value: (result as { secret: string }).secret });
      this.tokenName.set('');
    });
  }
  revokeToken(token: AccessToken): void { this.perform(this.api.revokeToken(token.id)); }
  createEnrollment(): void {
    this.perform(this.api.createEnrollment(this.runnerName().trim(), this.runnerKind()), result => {
      this.oneTime.set({ label: 'Runner enrollment code', value: (result as { enrollmentCode: string }).enrollmentCode });
      this.runnerName.set('');
    });
  }
  revokeEnrollment(enrollment: AccessEnrollment): void { this.perform(this.api.revokeEnrollment(enrollment.id)); }
  enrollmentActive(enrollment: AccessEnrollment): boolean {
    return !enrollment.usedAt && Date.parse(enrollment.expiresAt) > Date.now();
  }
  revokeRunner(runner: AccessRunner): void { this.perform(this.api.revokeRunner(runner.id)); }
  private projects(value: string): string[] { return value.split(',').map(x => x.trim()).filter(Boolean); }
  private perform(request: Observable<unknown>, after?: (value: unknown) => void): void {
    if (this.busy()) return;
    this.busy.set(true); this.error.set(null); this.oneTime.set(null);
    request.subscribe({ next: value => { after?.(value); this.refresh(); }, error: err => this.failed(err) });
  }
  private failed(err: unknown): void {
    if (err instanceof HttpErrorResponse && (err.status === 401 || err.status === 403)) {
      this.accessDenied.set(true);
      this.users.set([]); this.tokens.set([]);
      this.enrollments.set([]); this.runners.set([]);
      this.oneTime.set(null); this.editId.set(null);
    }
    this.busy.set(false);
    this.error.set((err as { error?: { message?: string }; message?: string })?.error?.message
      ?? (err as { message?: string })?.message ?? 'Access request failed.');
  }
}
