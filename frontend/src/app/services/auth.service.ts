import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, tap } from 'rxjs';

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
  /** The standalone Task Server arms an installer code for its first owner. */
  bootstrapCodeRequired?: boolean;
  user?: AuthUser | null;
}

interface StandaloneAuthUser {
  userId: string;
  username: string;
  displayName: string;
  role: AuthUser['role'];
  projectIds: string[];
  disabled: boolean;
  mustChangePassword: boolean;
}

interface StandaloneAuthStatus {
  bootstrapRequired: boolean;
  authenticated: boolean;
  bootstrapCodeRequired?: boolean;
  user?: StandaloneAuthUser | null;
}

interface StandaloneAuthSession {
  status: StandaloneAuthStatus;
  recoveryCode?: string;
}

function normalizeStatus(response: AuthStatus | StandaloneAuthStatus): AuthStatus {
  if ('profile' in response) return response;
  const user = response.user;
  return {
    profile: 'networked', bootstrapRequired: response.bootstrapRequired,
    authenticated: response.authenticated,
    bootstrapCodeRequired: response.bootstrapRequired && response.bootstrapCodeRequired === true,
    user: user ? {
      id: user.userId, username: user.username, displayName: user.displayName,
      role: user.role, projects: user.projectIds ?? [], disabled: user.disabled,
      mustChangePassword: user.mustChangePassword,
    } : null,
  };
}

function normalizeSession(response: AuthStatus | StandaloneAuthSession): AuthStatus {
  return 'status' in response ? normalizeStatus(response.status) : normalizeStatus(response);
}

/** Credential-free browser auth state shared with the 401 response interceptor. */
@Injectable({ providedIn: 'root' })
export class AuthSessionState {
  readonly status = signal<AuthStatus | null>(null);
  readonly loading = signal(true);
  /** One-time owner recovery code; memory only until the owner acknowledges custody. */
  readonly ownerRecoveryCode = signal<string | null>(null);
  readonly studioAllowed = computed(() => {
    const status = this.status();
    // public-demo-readonly has no sign-in flow at all - every visitor is an
    // anonymous, unauthenticated read-only viewer by design (W34 §8 S4). The
    // server edge (PublicDemoEdgeMiddleware) is the actual boundary; letting
    // the shell render here is what makes the demo browsable in the first
    // place.
    return !this.ownerRecoveryCode() && (status?.profile === 'local'
      || status?.profile === 'public-demo-readonly'
      || (status?.authenticated === true && !status.user?.mustChangePassword));
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
  readonly ownerRecoveryCode = this.session.ownerRecoveryCode;

  acknowledgeRecoveryCode(): void { this.ownerRecoveryCode.set(null); }

  initialize(): void {
    this.loading.set(true);
    this.http.get<AuthStatus | StandaloneAuthStatus>('/api/v1/studio/auth/status').subscribe({
      next: (status) => { this.status.set(normalizeStatus(status)); this.loading.set(false); },
      error: () => { this.status.set(null); this.loading.set(false); },
    });
  }

  login(username: string, password: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus | StandaloneAuthSession>('/api/v1/studio/auth/login', { username, password })
      .pipe(map(normalizeSession), tap((status) => this.status.set(status)));
  }

  bootstrap(username: string, password: string, displayName: string, bootstrapCode?: string): Observable<AuthStatus> {
    return this.http.post<AuthStatus | StandaloneAuthSession>('/api/v1/studio/auth/bootstrap',
      { username, password, displayName, ...(bootstrapCode ? { bootstrapCode } : {}) })
      .pipe(tap(response => {
        if ('recoveryCode' in response && response.recoveryCode)
          this.ownerRecoveryCode.set(response.recoveryCode);
      }), map(normalizeSession), tap((status) => this.status.set(status)));
  }

  changePassword(currentPassword: string, newPassword: string): Observable<AuthUser> {
    return this.http.post<AuthUser | StandaloneAuthUser>('/api/v1/studio/auth/change-password', { currentPassword, newPassword })
      .pipe(map(user => 'userId' in user ? {
        id: user.userId, username: user.username, displayName: user.displayName,
        role: user.role, projects: user.projectIds ?? [], disabled: user.disabled,
        mustChangePassword: user.mustChangePassword,
      } : user), tap((user) => {
        const status = this.status();
        if (status) this.status.set({ ...status, authenticated: true, user });
      }));
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/v1/studio/auth/logout', {})
      .pipe(tap(() => this.session.expireNetworkedSession()));
  }
}
