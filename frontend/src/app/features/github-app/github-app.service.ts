import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import type { Observable } from 'rxjs';

export type GitHubAppCoverageState =
  | 'covered'
  | 'not-installed'
  | 'not-adopted'
  | 'no-repository-url'
  | 'not-github'
  | 'unknown';

export interface GitHubAppInstallation {
  id: number;
  account: string;
  accountType: string;
  adoptedAtUtc: string;
  adoptedBy: string;
}

export interface GitHubAppRepositoryCoverage {
  project: string;
  repositoryUrl: string | null;
  state: GitHubAppCoverageState;
  installationId: number | null;
  detail: string | null;
}

export interface GitHubAppHostSync {
  runnerId: string;
  access: 'read' | 'write';
  repositories: number;
  lastSyncUtc: string;
}

/** Public identifiers only. The API never returns a key or a token to the browser. */
export interface GitHubAppStatus {
  connected: boolean;
  appId: number | null;
  slug: string | null;
  name: string | null;
  ownerLogin: string | null;
  htmlUrl: string | null;
  installUrl: string | null;
  connectedAtUtc: string | null;
  installations: readonly GitHubAppInstallation[];
  repositories: readonly GitHubAppRepositoryCoverage[];
  hosts: readonly GitHubAppHostSync[];
  problem: string | null;
}

export interface GitHubAppRegistrationForm {
  actionUrl: string;
  manifest: string;
  state: string;
}

export interface GitHubAppFlowStep {
  nextUrl: string | null;
  message: string | null;
  status: GitHubAppStatus;
}

/** What GitHub put into the return URL. */
export interface GitHubAppReturn {
  state: string | null;
  code: string | null;
  installationId: number | null;
  setupAction: string | null;
}

export interface ExistingGitHubApp {
  appId: number;
  clientId: string;
  clientSecret: string;
  privateKeyPem: string;
}

/** The two ways the flow leaves this page. A token, so specs never navigate. */
export interface GitHubAppNavigator {
  assign(url: string): void;
  postForm(action: string, fields: Readonly<Record<string, string>>): void;
}

export const GITHUB_APP_NAVIGATOR = new InjectionToken<GitHubAppNavigator>('GITHUB_APP_NAVIGATOR', {
  providedIn: 'root',
  factory: () => ({
    assign: url => window.location.assign(url),
    postForm: (action, fields) => {
      const form = document.createElement('form');
      form.method = 'post';
      form.action = action;
      form.style.display = 'none';
      for (const [name, value] of Object.entries(fields)) {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = value;
        form.append(input);
      }
      document.body.append(form);
      form.submit();
    },
  }),
});

/** Where GitHub returns the browser. The server redirects nothing: this page resumes the flow. */
export const GITHUB_APP_RETURN_PATH = '/github-app/callback';
export const GITHUB_APP_SETTINGS_ROUTE = '/workspace/settings/github';

/**
 * Reads GitHub's return parameters and removes them from the address bar, so a
 * single-use code stays out of the history and the settings route takes over.
 */
export function readGitHubAppReturn(
  location: Pick<Location, 'pathname' | 'search'>,
  history: Pick<History, 'replaceState'>,
): GitHubAppReturn | null {
  if (location.pathname !== GITHUB_APP_RETURN_PATH) return null;
  const query = new URLSearchParams(location.search);
  const installation = Number(query.get('installation_id'));
  const found: GitHubAppReturn = {
    state: query.get('state'),
    code: query.get('code'),
    installationId: Number.isSafeInteger(installation) && installation > 0 ? installation : null,
    setupAction: query.get('setup_action'),
  };
  history.replaceState(null, '', '/#' + GITHUB_APP_SETTINGS_ROUTE);
  return found;
}

/**
 * Settings > GitHub. Connects the App in the browser: registration, installation
 * and authorization are each a redirect to GitHub and back. The server holds
 * every secret; this service only forwards what GitHub put into the return URL.
 */
@Injectable({ providedIn: 'root' })
export class GitHubAppService {
  private readonly http = inject(HttpClient);
  private readonly navigator = inject(GITHUB_APP_NAVIGATOR);
  private static readonly root = '/api/v1/management/github-app';

  readonly status = signal<GitHubAppStatus | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  /** Parked by the app shell at startup, until the panel can resume it. */
  private pendingReturn: GitHubAppReturn | null = null;

  /** True when the page was opened by GitHub's redirect. */
  captureReturn(location: Pick<Location, 'pathname' | 'search'>, history: Pick<History, 'replaceState'>): boolean {
    const found = readGitHubAppReturn(location, history);
    if (found) this.pendingReturn = found;
    return found !== null;
  }

  load(refresh = false): void {
    this.http.get<GitHubAppStatus>(GitHubAppService.root + (refresh ? '?refresh=true' : '')).subscribe({
      next: status => {
        this.status.set(status);
        if (refresh) this.message.set(null);
      },
      error: failure => this.fail(failure),
    });
  }

  /** Resumes a flow GitHub just returned from. A no-op on a normal visit. */
  resumePending(): boolean {
    const pending = this.pendingReturn;
    this.pendingReturn = null;
    if (!pending) return false;
    this.run(this.http.post<GitHubAppFlowStep>(`${GitHubAppService.root}/flow`, { origin: this.origin(), ...pending }),
      step => this.applyStep(step));
    return true;
  }

  startRegistration(organization: string, anyAccount: boolean): void {
    this.run(this.http.post<GitHubAppRegistrationForm>(`${GitHubAppService.root}/registration`, {
      origin: this.origin(),
      organization: organization.trim() || null,
      anyAccount,
    }), form => this.navigator.postForm(form.actionUrl, { manifest: form.manifest }));
  }

  /** Where repositories or another account are added, on GitHub's own page. */
  startInstallation(): void {
    this.run(this.http.post<GitHubAppFlowStep>(`${GitHubAppService.root}/installation`, { origin: this.origin() }),
      step => this.applyStep(step));
  }

  /** Confirms with the person's GitHub account which installations belong to this Studio. */
  startAuthorization(): void {
    this.run(this.http.post<GitHubAppFlowStep>(`${GitHubAppService.root}/authorization`, { origin: this.origin() }),
      step => this.applyStep(step));
  }

  connectExisting(app: ExistingGitHubApp): void {
    this.run(this.http.put<GitHubAppStatus>(`${GitHubAppService.root}/credentials`, app), status => {
      this.status.set(status);
      this.message.set('The App is connected. Next, confirm its installations with your GitHub account.');
    });
  }

  disconnect(): void {
    this.run(this.http.delete<void>(GitHubAppService.root), () => {
      this.message.set('GitHub is disconnected. The App itself still exists on GitHub and can be deleted there.');
      this.load();
    });
  }

  private applyStep(step: GitHubAppFlowStep): void {
    this.status.set(step.status);
    this.message.set(step.message);
    if (step.nextUrl) this.navigator.assign(step.nextUrl);
  }

  private run<T>(request: Observable<T>, next: (value: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    this.message.set(null);
    request.subscribe({
      next: value => {
        this.busy.set(false);
        next(value);
      },
      error: failure => {
        this.busy.set(false);
        this.fail(failure);
      },
    });
  }

  private fail(failure: unknown): void {
    const body = failure instanceof HttpErrorResponse ? (failure.error as { message?: unknown } | null) : null;
    this.error.set(typeof body?.message === 'string' ? body.message : 'GitHub could not be reached through Studio.');
  }

  private origin(): string {
    return window.location.origin;
  }
}
