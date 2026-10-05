import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';

export interface AuthUser {
  id: string;
  username: string;
  displayName: string;
  role: 'owner' | 'operator' | 'viewer';
  projects: string[];
  disabled: boolean;
  mustChangePassword: boolean;
}

export interface AuthStatus {
  profile: 'local' | 'networked' | 'public-demo-readonly';
  bootstrapRequired: boolean;
  authenticated: boolean;
  user?: AuthUser | null;
}

/** Credential-free browser auth state shared with the 401 response interceptor. */
@Injectable({ providedIn: 'root' })
export class AuthSessionState {
  readonly status = signal<AuthStatus | null>(null);
  readonly loading = signal(true);
  readonly studioAllowed = computed(() => {
    const status = this.status();
    // public-demo-readonly has no sign-in flow at all - every visitor is an
    // anonymous, unauthenticated read-only viewer by design (W34 §8 S4). The
    // server edge (PublicDemoEdgeMiddleware) is the actual boundary; letting
    // the shell render here is what makes the demo browsable in the first
    // place.
    return status?.profile === 'local'
      || status?.profile === 'public-demo-readonly'
      || (status?.authenticated === true && !status.user?.mustChangePassword);
  });
  readonly networkedAuthenticated = computed(() => {
    const status = this.status();
    return status?.profile === 'networked' && status.authenticated === true;
  });

  expireNetworkedSession(): void {
    const status = this.status();
    if (status?.profile !== 'networked') return;
    this.status.set({
      profile: 'networked',
      bootstrapRequired: false,
      authenticated: false,
      user: null,
    });
  }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = inject(AuthSessionState);
  readonly status = this.session.status;
  readonly loading = this.session.loading;
  readonly studioAllowed = this.session.studioAllowed;
  readonly networkedAuthenticated = this.session.networkedAuthenticated;

  initialize(): void {
    this.loading.set(true);
    this.http.get<AuthStatus>('/api/v1/studio/auth/status').subscribe({
      next: (status) => { this.status.set(status); this.loading.set(false); },
      error: () => { this.status.set(null); this.loading.set(false); },
    });
  }

  login(username: string, password: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus>('/api/v1/studio/auth/login', { username, password })
      .pipe(tap((status) => this.status.set(status)));
  }

  bootstrap(username: string, password: string, displayName: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus>('/api/v1/studio/auth/bootstrap', { username, password, displayName })
      .pipe(tap((status) => this.status.set(status)));
  }

  changePassword(currentPassword: string, newPassword: string): Observable<AuthUser> {
    return this.http.post<AuthUser>('/api/v1/studio/auth/change-password', { currentPassword, newPassword })
      .pipe(tap((user) => {
        const status = this.status();
        if (status) this.status.set({ ...status, authenticated: true, user });
      }));
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/v1/studio/auth/logout', {})
      .pipe(tap(() => this.session.expireNetworkedSession()));
  }
}
