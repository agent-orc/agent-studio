import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

export interface AccessUser {
  id: string; username: string; displayName: string; role: string;
  projects: string[]; disabled: boolean; mustChangePassword: boolean;
}
export interface AccessToken {
  id: string; name: string; role: string; projects: string[]; createdBy: string;
  createdAt: string; expiresAt: string; lastUsedAt: string | null; revokedAt: string | null;
}
export interface AccessEnrollment {
  id: string; name: string; scopes: string[]; createdAt: string; expiresAt: string; usedAt: string | null;
}
export interface AccessRunner {
  id: string; name: string; createdAt: string; revokedAt: string | null;
  credentials: { id: string; scopes: string[]; expiresAt: string | null; lastUsedAt: string | null; revokedAt: string | null }[];
}

@Injectable({ providedIn: 'root' })
export class AccessSettingsService {
  private readonly http = inject(HttpClient);
  users() { return this.http.get<AccessUser[]>('/api/auth/users'); }
  invite(username: string, displayName: string, role: string, projects: string[]) {
    return this.http.post<{ user: AccessUser; temporaryPassword: string }>('/api/auth/users',
      { username, displayName, role, projects });
  }
  updateUser(user: AccessUser) {
    return this.http.put<AccessUser>(`/api/auth/users/${encodeURIComponent(user.id)}`,
      { displayName: user.displayName, role: user.role, projects: user.projects, disabled: user.disabled });
  }
  resetUser(id: string) {
    return this.http.post<{ temporaryPassword: string }>(`/api/auth/users/${encodeURIComponent(id)}/reset-password`, {});
  }
  tokens() { return this.http.get<AccessToken[]>('/api/auth/api-tokens'); }
  createToken(name: string, role: string, projects: string[], expiresAt: string) {
    return this.http.post<{ token: AccessToken; secret: string }>('/api/auth/api-tokens',
      { name, role, projects, expiresAt });
  }
  revokeToken(id: string) { return this.http.delete<void>(`/api/auth/api-tokens/${encodeURIComponent(id)}`); }
  enrollments() { return this.http.get<AccessEnrollment[]>('/api/auth/runner-enrollments'); }
  createEnrollment(name: string, kind: 'runner' | 'orchestrator') {
    const scopes = kind === 'orchestrator' ? ['orchestration.write'] : undefined;
    return this.http.post<{ enrollmentCode: string }>('/api/auth/runner-enrollments', { name, scopes });
  }
  revokeEnrollment(id: string) { return this.http.delete<void>(`/api/auth/runner-enrollments/${encodeURIComponent(id)}`); }
  runners() { return this.http.get<AccessRunner[]>('/api/auth/runners'); }
  revokeRunner(id: string) { return this.http.delete<void>(`/api/auth/runners/${encodeURIComponent(id)}`); }
}
