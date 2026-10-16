import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { DomSanitizer, type SafeResourceUrl } from '@angular/platform-browser';
import { finalize } from 'rxjs';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { TaskService } from '../../../../services/task.service';
import { AuthService } from '../../../../services/auth.service';
import {
  ProjectUrlProbeService,
  type ProjectUrlReadiness,
} from '../../../../services/project-url-probe.service';
import type { ProjectUrlDiagnostic, ProjectUrlSuggestion, RegistryProjectUrl } from '../../../../models/task.model';
import { ProjectUrlLookupService } from '../../services/project-url-lookup.service';
import { ProjectUrlProcessController } from '../../services/project-url-process.controller';
import { ProjectUrlEmbedNavigationController } from '../../services/project-url-embed-navigation.controller';
import { ProjectUrlAddressComponent } from '../project-url-address/project-url-address';
import { ProjectUrlRecoveryService } from '../../services/project-url-recovery.service';
import { ProjectUrlProcessConsoleComponent } from '../project-url-process-console/project-url-process-console';
import { ProjectUrlContextHeaderComponent } from '../project-url-context-header/project-url-context-header';
import {
  ProjectUrlSettingsDialogComponent,
  type ProjectUrlSettingsValue,
} from '../project-url-settings-dialog/project-url-settings-dialog';
import { buildEmbedMenuItems, httpErrorMessage, projectPreviewProxyUrl, type StartFailure } from './project-url-preview-tab.helpers';
import { diagnosticText, presentProjectUrlDiagnosis } from './project-url-diagnostic.model';

type PreviewPill = 'running' | 'offline' | 'checking' | 'building' | 'blocked' | 'failed' | 'custom';

/** Readiness kinds that warrant loading the full backend diagnosis. */
const DIAGNOSABLE_KINDS: ReadonlySet<ProjectUrlReadiness['kind']> =
  new Set(['offline', 'timeout', 'http-error', 'frame-blocked']);

/**
 * AGT-2067 — embedded Project URL preview tab.
 *
 * Renders a configured Project URL inside a **sandboxed `<iframe>`** as its own
 * editor tab (one tab per URL), replacing the old `window.open` browser jump.
 * Opening the Orchestrator side sheet beside it *is* the split view — this pane
 * owns the address bar (URL, status pill, reload, open-externally, settings)
 * plus the iframe and its load / offline / blocked state machine.
 *
 * State machine (concept §1.3), driven by {@link ProjectUrlProbeService} plus
 * the iframe `load` event and a load-timeout heuristic: `resolving` (registry
 * lookup), `not-found` (URL removed), resolved + offline ("Not running" card,
 * Start button when a start rule exists), resolved + healthy (mount the
 * iframe; if `load` never fires within {@link LOAD_TIMEOUT_MS} while the
 * server is reachable, surface an embed-refusal banner, X-Frame-Options/CSP).
 *
 * AGT-2180 layers actionable diagnostics on the failure states: whenever the
 * readiness probe reports a failure kind, the backend diagnostic endpoint is
 * queried and its classification, recommended action, and redacted evidence
 * enrich the offline card. A repository-derived quick fix can be applied in
 * place, and the Settings deep link hands the failing URL (plus any detected
 * suggestion) to the Settings "URL Preview quick setup" section.
 */
@Component({
  selector: 'app-project-url-preview-tab',
  standalone: true,
  imports: [
    StudioIconComponent,
    TooltipDirective,
    AppTooltipDirective,
    MenuComponent,
    ProjectUrlAddressComponent,
    ProjectUrlContextHeaderComponent,
    ProjectUrlProcessConsoleComponent,
    ProjectUrlSettingsDialogComponent,
  ],
  providers: [ProjectUrlProcessController, ProjectUrlEmbedNavigationController],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Emulated encapsulation on purpose: the shell's `.studio button` reset
  // outranks bare classes and would flatten the card's action buttons.
  templateUrl: './project-url-preview-tab.component.html',
  styleUrl: './project-url-preview-tab.component.scss',
})
export class ProjectUrlPreviewTabComponent {
  readonly projectName = input.required<string>();
  readonly urlId = input.required<string>();

  /** Deep-link request to the Project Hub "Project URLs" management page. */
  readonly openSettings = output<{ projectName: string }>();

  private readonly lookup = inject(ProjectUrlLookupService);
  private readonly probe = inject(ProjectUrlProbeService);
  private readonly recovery = inject(ProjectUrlRecoveryService);
  private readonly taskService = inject(TaskService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly auth = inject(AuthService);
  readonly networked = computed(() => this.auth.status()?.profile === 'networked');
  readonly process = inject(ProjectUrlProcessController);
  readonly nav = inject(ProjectUrlEmbedNavigationController);
  /** ~6s without `load` while reachable reads as "probably refuses embedding". */
  private readonly LOAD_TIMEOUT_MS = 6000;

  readonly resolveState = signal<'resolving' | 'resolved' | 'not-found' | 'error'>('resolving');
  readonly projectId = signal<string | null>(null);
  readonly urlRecord = signal<RegistryProjectUrl | null>(null);
  readonly frameState = signal<'loading' | 'loaded' | 'blocked'>('loading');
  readonly building = signal(false);
  readonly repositoryPath = signal<string | null>(null);
  readonly rootPath = signal<string | null>(null);
  readonly startFailure = signal<StartFailure | null>(null);
  readonly menuOpen = signal(false);
  readonly menuPosition = signal<{ x: number; y: number } | null>(null);
  readonly settingsOpen = signal(false);
  readonly settingsSaving = signal(false);
  readonly settingsError = signal<string | null>(null);
  /** AGT-2180 — latest backend diagnosis for the failure card, if fetched. */
  readonly diagnostic = signal<ProjectUrlDiagnostic | null>(null);
  readonly diagnosis = computed(() => presentProjectUrlDiagnosis(this.diagnostic()));
  /** AGT-2180 — repository-derived quick fix detected for the failing URL. */
  readonly detectedSuggestion = signal<ProjectUrlSuggestion | null>(null);
  readonly detailsOpen = signal(false);
  readonly copied = signal(false);
  /** Bumping this re-navigates the iframe (forces a fresh `[src]` reference). */
  private readonly reloadNonce = signal(0);
  private readonly frameRef = viewChild<ElementRef<HTMLIFrameElement>>('frame');
  private loadTimer: ReturnType<typeof setTimeout> | null = null;
  /** De-dupes diagnostic fetches per (project, url, readiness-kind) tuple. */
  private lastDiagnosedKey: string | null = null;

  readonly readiness = computed<ProjectUrlReadiness>(() => {
    const projectId = this.projectId();
    const url = this.urlRecord();
    return projectId && url
      ? this.probe.readinessFor(projectId, url.id)
      : { kind: 'unknown', statusCode: null, framePolicy: 'unknown', detail: null, durationMs: null };
  });

  readonly probeStatus = computed(() => {
    const projectId = this.projectId();
    const u = this.urlRecord();
    return projectId && u ? this.probe.statusFor(projectId, u.id) : 'unknown';
  });

  /** The URL the frame actually shows: the typed override, else the record. */
  readonly effectiveUrl = computed(() => {
    const u = this.urlRecord();
    return u ? this.nav.overrideUrl() ?? u.url : null;
  });

  /** Address-bar text: the embed-reported live URL wins over the mounted one. */
  readonly displayUrl = computed(() => this.nav.reportedUrl() ?? this.effectiveUrl());
  readonly proxyUrl = computed(() => projectPreviewProxyUrl(this.projectId(), this.urlId()));

  /** Embed once resolved and healthy; offline shows the card. A typed
   *  override embeds unconditionally (readiness probes only the record URL). */
  readonly shouldEmbed = computed(() =>
    this.resolveState() === 'resolved'
      && !this.building()
      && !this.startFailure()
      && (this.readiness().kind === 'healthy'
        || (this.networked() && this.readiness().kind === 'frame-blocked')
        || this.nav.overrideUrl() !== null),
  );

  readonly readinessPending = computed(() =>
    this.resolveState() === 'resolved'
      && !this.building()
      && !this.startFailure()
      && this.readiness().kind === 'unknown',
  );

  /** Sandboxed iframe source; recomputes on reload, never on probe re-ticks. */
  readonly iframeSrc = computed<SafeResourceUrl | null>(() => {
    const target = this.networked() ? this.proxyUrl() : this.effectiveUrl();
    if (!target) return null;
    void this.reloadNonce();
    return this.sanitizer.bypassSecurityTrustResourceUrl(target);
  });

  readonly statusPill = computed<PreviewPill>(() => {
    if (this.building()) return 'building';
    if (this.startFailure()) return 'failed';
    if (this.diagnostic()?.startupFailureReason) return 'failed';
    if (this.resolveState() !== 'resolved') return 'checking';
    if (this.nav.overrideUrl()) return 'custom';
    if (this.frameState() === 'blocked') return 'blocked';
    const s = this.probeStatus();
    return s === 'unknown' ? 'checking' : s;
  });

  readonly stateBadgeStatus = computed<PreviewPill>(() => {
    if (this.startFailure()) return 'failed';
    if (this.building()) return 'building';
    if (this.diagnostic()?.startupFailureReason) return 'failed';
    if (this.readiness().kind === 'frame-blocked') return 'blocked';
    if (this.readiness().kind === 'http-error') return 'failed';
    return 'offline';
  });

  readonly canReload = computed(() => this.resolveState() === 'resolved' && !this.building());
  /** While the console shows command/cwd below, the card skips its copy. */
  readonly consoleVisible = computed(() => this.process.consoleOpen() && this.process.session() !== null);
  readonly consoleActive = computed(() =>
    this.process.session()?.output.some(line => !line.startsWith('[studio]')) ?? false);

  readonly menuItems = computed<readonly MenuItem[]>(() => buildEmbedMenuItems({
    url: this.urlRecord(),
    session: this.process.session(),
    probeRunning: this.probeStatus() === 'running',
    building: this.building(),
    stopping: this.process.stopping(),
  }));

  constructor() {
    // Re-resolve whenever the bound project / url inputs change.
    effect(() => {
      const name = this.projectName();
      const id = this.urlId();
      this.resolveRecord(name, id);
    });

    // On every (re)mount: re-arm the load timeout, drop the embed-reported URL.
    effect(() => {
      const embed = this.shouldEmbed();
      this.reloadNonce(); // re-arm on reload
      this.clearLoadTimer();
      this.nav.clearReported();
      if (!embed) return;
      this.frameState.set('loading');
      this.loadTimer = setTimeout(() => this.onLoadTimeout(), this.LOAD_TIMEOUT_MS);
    });

    // Trust embed-reported navigations only from the mounted preview iframe.
    effect(() => this.nav.attachFrame(this.frameRef()?.nativeElement ?? null));

    // Backend readiness owns the long-running decision. The process snapshot
    // stays "starting" while recent console activity extends the wait, then
    // settles as running, exited, or failed (silence/startup limit).
    effect(() => {
      const session = this.process.session();
      const projectId = this.projectId();
      const url = this.urlRecord();
      if (!session || !projectId || !url || session.urlId !== url.id) return;
      if (session.state === 'starting') {
        this.building.set(true);
        return;
      }
      if (session.state === 'running') {
        if (!this.building()) return;
        this.building.set(false);
        this.probe.refresh(projectId, url.id);
        return;
      }
      if ((session.state === 'exited' || session.state === 'failed') && this.building()) {
        this.building.set(false);
        this.fetchDiagnostic(projectId, url.id);
      }
    });

    // AGT-2180: whenever the readiness probe settles on a failure kind, fetch
    // the full backend diagnosis (once per kind) so the offline card can show
    // classification, recommended action, and redacted evidence.
    effect(() => {
      const projectId = this.projectId();
      const url = this.urlRecord();
      const kind = this.readiness().kind;
      if (!projectId || !url || this.resolveState() !== 'resolved') return;
      if (this.building() || !DIAGNOSABLE_KINDS.has(kind)) return;
      this.fetchDiagnostic(projectId, url.id);
    });

    inject(DestroyRef).onDestroy(() => {
      this.clearLoadTimer();
    });
  }

  private resolveRecord(name: string, id: string): void {
    this.process.reset();
    this.building.set(false);
    this.startFailure.set(null);
    this.nav.reset();
    this.menuOpen.set(false);
    this.settingsOpen.set(false);
    this.diagnostic.set(null);
    this.detectedSuggestion.set(null);
    this.lastDiagnosedKey = null;
    this.resolveState.set('resolving');
    this.lookup.resolve(name, id).subscribe({
      next: res => {
        // Guard against a stale response after the inputs changed again.
        if (name !== this.projectName() || id !== this.urlId()) return;
        if (!res) {
          this.projectId.set(null);
          this.urlRecord.set(null);
          this.repositoryPath.set(null);
          this.rootPath.set(null);
          this.resolveState.set('not-found');
          return;
        }
        this.projectId.set(res.projectId);
        this.urlRecord.set(res.url);
        this.repositoryPath.set(res.repositoryPath);
        this.rootPath.set(res.rootPath);
        this.frameState.set('loading');
        this.resolveState.set('resolved');
        this.process.refresh(res.projectId, res.url.id);
      },
      error: () => {
        if (name !== this.projectName() || id !== this.urlId()) return;
        this.resolveState.set('error');
      },
    });
  }

  /** iframe finished (loaded a page — content unreadable cross-origin, only that
   *  it navigated). Clears the suspected-block timer. */
  onFrameLoad(event?: Event): void {
    if (this.nav.overrideUrl() === null && this.probeStatus() !== 'running') return;
    const frame = event?.target as HTMLIFrameElement | null;
    try {
      // Same-origin only: a blank document is not a usable preview even when
      // the transport probe reported healthy renderable content.
      const body = frame?.contentDocument?.body;
      if (body && body.children.length === 0 && !body.textContent?.trim()) {
        this.frameState.set('blocked');
        this.diagnostic.update(value => value ? {
          ...value,
          classification: 'content-not-renderable',
          summary: 'The server returned a blank page that cannot be previewed.',
          recommendedAction: 'Correct the page URL or open the target externally.',
          contentReady: false, iframeReady: false,
        } : value);
        this.clearLoadTimer();
        return;
      }
    } catch {
      // Cross-origin frames are intentionally opaque. Backend content
      // readiness and the bounded iframe timeout remain authoritative.
    }
    this.frameState.set('loaded');
    this.diagnostic.update(value => value ? { ...value, iframeReady: true } : value);
    this.clearLoadTimer();
  }

  /** Enter in the address bar: navigate this preview session only. */
  onNavigate(target: string): void {
    const u = this.urlRecord();
    if (!u) return;
    if (this.networked()) return;
    this.nav.navigate(target, u.url);
    this.frameState.set('loading');
    this.reloadNonce.update(n => n + 1);
  }

  private onLoadTimeout(): void {
    if (this.frameState() !== 'loading') return;
    // Only escalate to "blocked" when the server is actually reachable; an
    // unreachable server is an offline case, not an embed refusal.
    if (this.probeStatus() === 'running') {
      const url = this.urlRecord()?.url;
      console.warn('[url-preview] suspected embed refusal (X-Frame-Options / CSP)', { url });
      this.frameState.set('blocked');
    }
  }

  reload(): void {
    if (!this.canReload()) return;
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url) return;
    const remount = this.probeStatus() === 'running' || this.nav.overrideUrl() !== null;
    this.lastDiagnosedKey = null;
    this.probe.refresh(projectId, url.id);
    if (remount) {
      this.frameState.set('loading');
      this.reloadNonce.update(n => n + 1);
    }
  }

  /** Start or restart the owned process, expose its output in place, and keep
   * backend-owned activity readiness before mounting the iframe. */
  start(): void {
    const projId = this.projectId();
    const u = this.urlRecord();
    if (!projId || !u || !u.startRule || this.building() || this.process.stopping()) return;
    const rule = u.startRule;
    this.startFailure.set(null);
    this.lastDiagnosedKey = null;
    this.building.set(true);
    this.process.start(projId, u, this.effectiveCwd()).subscribe({
      next: () => this.afterStart(u.id),
      error: (error: HttpErrorResponse) => {
        const body: Record<string, unknown> = error.error && typeof error.error === 'object'
          ? error.error as Record<string, unknown>
          : {};
        const failure: StartFailure = {
          explanation: typeof body['error'] === 'string'
            ? body['error']
            : 'The dev server could not be started.',
          command: typeof body['command'] === 'string' ? body['command'] : rule.command,
          cwd: typeof body['cwd'] === 'string' && body['cwd'].trim()
            ? body['cwd']
            : this.effectiveCwd(),
        };
        this.startFailure.set(failure);
        this.process.failStart(failure.explanation, failure.command, failure.cwd);
        this.building.set(false);
        this.fetchDiagnostic(projId, u.id);
      },
    });
  }

  /** AGT-2180 — re-resolve possibly corrected setup, then start or re-diagnose. */
  retry(): void {
    if (this.building()) return;
    const name = this.projectName();
    const id = this.urlId();
    this.building.set(true);
    this.lookup.resolve(name, id).subscribe({
      next: res => {
        if (name !== this.projectName() || id !== this.urlId()) return;
        this.building.set(false);
        if (!res) {
          this.resolveState.set('not-found');
          return;
        }
        this.projectId.set(res.projectId);
        this.urlRecord.set(res.url);
        this.startFailure.set(null);
        this.lastDiagnosedKey = null;
        if (res.url.startRule) this.start();
        else this.fetchDiagnostic(res.projectId, res.url.id);
      },
      error: () => {
        if (name !== this.projectName() || id !== this.urlId()) return;
        this.building.set(false);
      },
    });
  }

  /** AGT-2180 — apply the detected repository-derived setup, then start it. */
  applyDetectedSetup(): void {
    const projId = this.projectId();
    const url = this.urlRecord();
    const suggestion = this.detectedSuggestion();
    if (!projId || !url || !suggestion || this.building()) return;
    this.building.set(true);
    this.recovery.apply(projId, url, suggestion).subscribe({
      next: corrected => {
        this.building.set(false);
        if (corrected) this.urlRecord.set(corrected);
        this.startFailure.set(null);
        this.lastDiagnosedKey = null;
        if (corrected?.startRule) this.start();
        else if (corrected) this.fetchDiagnostic(projId, corrected.id);
      },
      error: error => {
        this.building.set(false);
        this.settingsError.set(httpErrorMessage(error));
      },
    });
  }

  /** AGT-2180 — copy the full redacted diagnostic evidence to the clipboard. */
  async copyDiagnostics(): Promise<void> {
    const value = this.diagnostic();
    if (!value) return;
    await navigator.clipboard.writeText(diagnosticText(value));
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1500);
  }

  stop(): void {
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url || this.process.stopping()) return;
    this.building.set(false);
    this.startFailure.set(null);
    this.process.stop(projectId, url.id).subscribe({
      next: () => this.probe.refresh(projectId, url.id),
      error: error => this.process.appendError(httpErrorMessage(error)),
    });
  }

  showMenu(event?: MouseEvent): void {
    // Right-click on the address input keeps the native menu (copy/paste).
    if (event?.target instanceof HTMLInputElement) return;
    event?.preventDefault();
    this.menuPosition.set(event ? { x: event.clientX, y: event.clientY } : null);
    this.menuOpen.set(true);
    const projectId = this.projectId();
    const urlId = this.urlRecord()?.id;
    if (projectId && urlId) this.process.refresh(projectId, urlId);
  }

  onMenuItem(event: MenuItemClickEvent): void {
    switch (event.id) {
      case 'start': this.start(); break;
      case 'console': this.process.consoleOpen.set(true); break;
      case 'stop': this.process.consoleOpen.set(true); this.stop(); break;
      case 'settings': this.onSettings(); break;
      case 'external': this.openExternal(); break;
    }
  }

  effectiveCwd(): string {
    return this.urlRecord()?.startRule?.cwd
      || this.repositoryPath()
      || this.rootPath()
      || 'Not configured';
  }

  private afterStart(urlId: string): void {
    const projectId = this.projectId();
    if (!projectId) return;
    this.probe.refresh(projectId, urlId);
  }

  /** AGT-2180 — load the backend diagnosis and, when it points at a fixable
   *  configuration, look for a repository-derived quick fix. */
  private fetchDiagnostic(projectId: string, urlId: string): void {
    const key = `${projectId}::${urlId}::${this.readiness().kind}`;
    if (key === this.lastDiagnosedKey) return;
    this.lastDiagnosedKey = key;
    this.taskService.diagnoseProjectUrl(projectId, urlId).subscribe({
      next: result => {
        if (this.projectId() !== projectId || this.urlRecord()?.id !== urlId) return;
        this.diagnostic.set(result);
        this.loadSuggestionFor(result);
      },
      error: () => { /* the readiness card remains authoritative without evidence */ },
    });
  }

  private loadSuggestionFor(result: ProjectUrlDiagnostic): void {
    const fixable: readonly ProjectUrlDiagnostic['classification'][] =
      ['invalid-configuration', 'invalid-cwd', 'command-unavailable', 'port-in-use', 'port-never-opened', 'not-started'];
    if (!fixable.includes(result.classification)) return;
    const projectId = this.projectId();
    const url = this.urlRecord();
    if (!projectId || !url) return;
    this.detectedSuggestion.set(null);
    this.recovery.detect(projectId, url).subscribe({
      next: suggestion => this.detectedSuggestion.set(suggestion),
    });
  }

  openExternal(): void {
    const target = this.networked() ? this.proxyUrl() : this.displayUrl();
    if (target) window.open(target, '_blank', 'noopener');
  }

  onSettings(): void {
    if (!this.urlRecord()) {
      this.openSettings.emit({ projectName: this.projectName() });
      return;
    }
    this.settingsError.set(null);
    this.settingsOpen.set(true);
  }

  /** AGT-2180 — hand the failing URL and any detected suggestion to the
   *  Settings "URL Preview quick setup" section, then deep-link there. */
  openQuickSetup(): void {
    this.recovery.requestQuickSetup(this.urlRecord()?.id ?? this.urlId(), this.detectedSuggestion());
    this.openSettings.emit({ projectName: this.projectName() });
  }

  saveSettings(value: ProjectUrlSettingsValue): void {
    const projectId = this.projectId();
    const current = this.urlRecord();
    if (!projectId || !current || this.settingsSaving()) return;
    this.settingsSaving.set(true);
    this.settingsError.set(null);
    this.taskService.updateProjectUrl(projectId, current.id, value).pipe(
      finalize(() => this.settingsSaving.set(false)),
    ).subscribe({
      next: () => {
        this.urlRecord.set({ ...current, ...value });
        this.nav.reset();
        this.settingsOpen.set(false);
        this.frameState.set('loading');
        this.lastDiagnosedKey = null;
        this.reloadNonce.update(n => n + 1);
        this.probe.refresh(projectId, current.id);
      },
      error: error => this.settingsError.set(httpErrorMessage(error)),
    });
  }

  private clearLoadTimer(): void {
    if (this.loadTimer) {
      clearTimeout(this.loadTimer);
      this.loadTimer = null;
    }
  }

}
