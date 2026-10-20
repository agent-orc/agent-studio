import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom, type Observable } from 'rxjs';
import type {
  AccessGroup,
  AccessOverview,
  AccessProject,
  EnrolledRunner,
  IamGrant,
  IamPrincipalKind,
  IamScope,
  InvitationResult,
  OneTimeServiceAccountToken,
  RoleBinding,
  RunnerEnrollment,
  ServiceAccount,
} from '../models/access.model';

const IAM = '/api/auth/iam';

/** Access area state: one overview read, mutations that reload it, and the last error. */
@Injectable({ providedIn: 'root' })
export class AccessService {
  private readonly http = inject(HttpClient);

  readonly overview = signal<AccessOverview | null>(null);
  readonly projects = signal<readonly AccessProject[]>([]);
  readonly enrollments = signal<readonly RunnerEnrollment[]>([]);
  readonly runners = signal<readonly EnrolledRunner[]>([]);
  readonly state = signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>('idle');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly pendingInvitations = computed(() => {
    const overview = this.overview();
    if (!overview) return new Set<string>();
    return new Set(overview.invitations.filter(invitation => !invitation.used).map(invitation => invitation.userId));
  });

  async load(): Promise<void> {
    this.state.set('loading');
    this.error.set(null);
    try {
      const [overview, projects, enrollments, runners] = await Promise.all([
        firstValueFrom(this.http.get<AccessOverview>(IAM)),
        firstValueFrom(this.http.get<AccessProject[]>('/api/projects')).catch(() => [] as AccessProject[]),
        firstValueFrom(this.http.get<RunnerEnrollment[]>('/api/auth/runner-enrollments')).catch(() => [] as RunnerEnrollment[]),
        firstValueFrom(this.http.get<EnrolledRunner[]>('/api/auth/runners')).catch(() => [] as EnrolledRunner[]),
      ]);
      this.overview.set(overview);
      this.projects.set(projects.map(project => ({ id: project.id, displayName: project.displayName })));
      this.enrollments.set(enrollments);
      this.runners.set(runners);
      this.state.set('ready');
    } catch (error: unknown) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      this.state.set(status === 401 || status === 403 ? 'denied' : 'error');
      this.error.set(status === 401
        ? 'Sign in to manage access. A local installation without sign-in has one implicit owner and needs no setup.'
        : status === 403 ? 'Your account cannot view access settings.'
          : status === 404 ? 'This Studio uses a standalone Task Server, which keeps its own accounts.'
            : message(error, 'Access settings could not be loaded.'));
    }
  }

  invite(username: string, displayName: string, bindings: readonly IamGrant[]): Promise<InvitationResult | null> {
    return this.mutate(this.http.post<InvitationResult>('/api/auth/invitations', {
      username, displayName, role: '', projects: null, bindings,
    }));
  }

  addBinding(principalKind: IamPrincipalKind, principalId: string, role: string, scope: IamScope): Promise<RoleBinding | null> {
    return this.mutate(this.http.post<RoleBinding>(`${IAM}/bindings`, { principalKind, principalId, role, scope }));
  }

  removeBinding(id: string): Promise<unknown> {
    return this.mutate(this.http.delete(`${IAM}/bindings/${encodeURIComponent(id)}`));
  }

  createGroup(name: string, description: string, members: readonly string[]): Promise<AccessGroup | null> {
    return this.mutate(this.http.post<AccessGroup>(`${IAM}/groups`, { name, description, members }));
  }

  updateGroupMembers(id: string, members: readonly string[]): Promise<AccessGroup | null> {
    return this.mutate(this.http.put<AccessGroup>(`${IAM}/groups/${encodeURIComponent(id)}`, { members }));
  }

  deleteGroup(id: string): Promise<unknown> {
    return this.mutate(this.http.delete(`${IAM}/groups/${encodeURIComponent(id)}`));
  }

  createServiceAccount(name: string, description: string, bindings: readonly IamGrant[]): Promise<ServiceAccount | null> {
    return this.mutate(this.http.post<ServiceAccount>(`${IAM}/service-accounts`, { name, description, bindings }));
  }

  createToken(accountId: string, name: string, expiresAt: string | null): Promise<OneTimeServiceAccountToken | null> {
    return this.mutate(this.http.post<OneTimeServiceAccountToken>(
      `${IAM}/service-accounts/${encodeURIComponent(accountId)}/tokens`, { name, expiresAt }));
  }

  revokeToken(accountId: string, tokenId: string): Promise<unknown> {
    return this.mutate(this.http.delete(
      `${IAM}/service-accounts/${encodeURIComponent(accountId)}/tokens/${encodeURIComponent(tokenId)}`));
  }

  disableServiceAccount(accountId: string): Promise<unknown> {
    return this.mutate(this.http.delete(`${IAM}/service-accounts/${encodeURIComponent(accountId)}`));
  }

  /** A new one-time password; the old sessions of the user end. */
  resetPassword(userId: string): Promise<{ temporaryPassword: string } | null> {
    return this.mutate(this.http.post<{ temporaryPassword: string }>(
      `/api/auth/users/${encodeURIComponent(userId)}/reset-password`, {}));
  }

  /** Sends only `disabled`, so the user's role bindings stay as they are. */
  setUserDisabled(userId: string, disabled: boolean): Promise<unknown> {
    return this.mutate(this.http.put(`/api/auth/users/${encodeURIComponent(userId)}`, { disabled }));
  }

  createEnrollment(name: string, kind: 'runner' | 'orchestrator'): Promise<{ enrollmentCode: string } | null> {
    const scopes = kind === 'orchestrator' ? ['orchestration.write'] : undefined;
    return this.mutate(this.http.post<{ enrollmentCode: string }>('/api/auth/runner-enrollments', { name, scopes }));
  }

  revokeEnrollment(id: string): Promise<unknown> {
    return this.mutate(this.http.delete(`/api/auth/runner-enrollments/${encodeURIComponent(id)}`));
  }

  revokeRunner(id: string): Promise<unknown> {
    return this.mutate(this.http.delete(`/api/auth/runners/${encodeURIComponent(id)}`));
  }

  /** Runs one mutation, reloads the overview on success, and keeps the server's message on failure. */
  private async mutate<T>(request: Observable<T>): Promise<T | null> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await firstValueFrom(request);
      await this.load();
      return result;
    } catch (error: unknown) {
      this.error.set(message(error, 'The change could not be saved.'));
      return null;
    } finally {
      this.busy.set(false);
    }
  }
}

function message(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: unknown } | null;
    if (body && typeof body.message === 'string' && body.message) return body.message;
  }
  return fallback;
}
