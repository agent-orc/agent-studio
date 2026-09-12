import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import {
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  DocumentWorkbenchResult,
} from '../../../../models/project-docs.model';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { WorkbenchViewerHeaderComponent } from '../workbench-viewer-header/workbench-viewer-header.component';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_ANCHOR_MESSAGE,
  WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE,
  WORKBENCH_DECISION_HOST_HEIGHT_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  WORKBENCH_DECISION_READY_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';
import { resolveWikiImageSrc } from '../../../../services/wiki-image-resolver';
import {
  discoverWorkbenchDecisionMarkup,
  normalizeWorkbenchDecisionResponses,
} from '../../../../services/workbench-decision-markup.util';
import { WorkbenchDecisionDraftStore } from '../../state/workbench-decision-draft.store';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';

/**
 * Trusted host chrome around repository-authored HTML. The artifact receives an
 * opaque origin (`allow-scripts` without `allow-same-origin`) and a deny-by-
 * default CSP. No credential, API, form, direct navigation, download, popup,
 * modal, or clipboard capability is bridged into the frame. Link clicks cross
 * one typed host boundary: docs-relative targets open in the Wiki and absolute
 * HTTP(S) targets open in a separate browser tab.
 */
@Component({
  selector: 'app-workbench-viewer',
  standalone: true,
  imports: [
    PendingButtonDirective,
    StudioIconComponent,
    WorkbenchViewerHeaderComponent,
    WorkbenchDecisionPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer.component.html',
  styleUrl: './workbench-viewer.component.scss',
})
export class WorkbenchViewerComponent {
  readonly projectName = input.required<string>();
  readonly workbenchId = input.required<string>();
  readonly openWiki = output<string>();
  /**
   * Emitted once the Dossier document resolves. A route-opened tab (deep
   * link or reload) starts without the catalogue `key`/title the Explorer
   * already knows for a click-opened tab; the host patches the active tab
   * with this resolved data so the orchestrator side sheet's Dossier scope
   * (AGT-2725) sees the same key regardless of how the tab was opened.
   */
  readonly documentResolved = output<WorkbenchDocument>();
  private readonly docs = inject(ProjectDocsService);
  private readonly http = inject(HttpClient);
  private readonly hub = inject(JobsHubClient);
  private readonly decisionDrafts = inject(WorkbenchDecisionDraftStore);
  private readonly publicDemo = inject(PublicDemoModeService);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('workbenchFrame');
  private readonly inlineActionHost = viewChild<ElementRef<HTMLElement>>('inlineActionHost');
  private requestGeneration = 0;

  readonly document = signal<WorkbenchDocument | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly maximized = signal(false);
  readonly decisionResponses = signal<WorkbenchDecisionResponse[]>([]);
  readonly documenting = signal(false);
  readonly documentationError = signal<string | null>(null);
  readonly decisionAnchor = signal<{
    top: number;
    left: number;
    width: number;
    visible: boolean;
  } | null>(null);
  /** Timestamp of the last successful Dossier read, used by the offline as-of line. */
  readonly lastUpdatedAtUtc = signal<string | null>(null);
  readonly liveConnected = this.hub.connected;
  readonly connectionChangedAtUtc = this.hub.connectionChangedAtUtc;
  readonly readOnly = this.publicDemo.readOnly;

  readonly srcdoc = computed(() => {
    const document = this.document();
    const project = this.projectName();
    const docRelPath = workbenchDocRelPath(document?.workbench.entryPath);
    return buildIsolatedHtmlSrcdoc(document?.html ?? '', {
      workbenchDecisions: true,
      documentPattern: document?.workbench.pattern === 'ui' ? 'ui' : 'concept',
      resolveAssetSrc: docRelPath
        ? (src) => resolveWikiImageSrc(src, docRelPath, (rel) => this.docs.wikiAssetUrl(project, rel))
        : undefined,
    });
  });
  readonly decisionMarkup = computed(() =>
    discoverWorkbenchDecisionMarkup(this.document()?.html ?? ''),
  );

  constructor() {
    effect(() => {
      const frame = this.frame();
      // This is the single audited HTML sink. The fixed wrapper is assigned as
      // a native string so Angular cannot remove the artifact's inline scripts.
      if (frame) frame.nativeElement.srcdoc = this.srcdoc();
    });
    effect(() => {
      const project = this.projectName();
      const id = this.workbenchId();
      this.maximized.set(false);
      this.loadDocument(project, id);
    });
    effect((onCleanup) => {
      const host = this.inlineActionHost()?.nativeElement;
      if (!host || typeof ResizeObserver === 'undefined') return;
      const publishHeight = () => this.frame()?.nativeElement.contentWindow?.postMessage({
        type: WORKBENCH_DECISION_HOST_HEIGHT_MESSAGE,
        height: host.getBoundingClientRect().height + 8,
      }, '*');
      const observer = new ResizeObserver(publishHeight);
      observer.observe(host);
      publishHeight();
      onCleanup(() => observer.disconnect());
    });
    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event) return;
      const project = this.projectName();
      const id = this.workbenchId();
      if (event.projectName && event.projectName !== project) return;
      if (event.workbenchId && event.workbenchId !== id) return;
      this.loadDocument(project, id, false);
    });
  }

  @HostListener('window:message', ['$event'])
  onFrameMessage(event: MessageEvent): void {
    const frameWindow = this.frame()?.nativeElement.contentWindow;
    if (!frameWindow || event.source !== frameWindow) return;
    const message = event.data as {
      type?: unknown;
      href?: unknown;
      responses?: unknown;
      rect?: unknown;
      visible?: unknown;
    } | null;
    if (message?.type === WORKBENCH_DECISION_READY_MESSAGE) {
      this.hydrateFrame();
      return;
    }
    if (message?.type === WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE) {
      this.inlineActionHost()?.nativeElement
        .querySelector<HTMLElement>('[data-testid="workbench-decision-start"]')
        ?.focus();
      return;
    }
    if (message?.type === WORKBENCH_DECISION_ANCHOR_MESSAGE) {
      const rect = message.rect as Partial<DOMRect> | null;
      if (!rect || !finite(rect.top) || !finite(rect.left) || !finite(rect.width)) return;
      this.decisionAnchor.set({
        top: rect.top!,
        left: rect.left!,
        width: Math.max(240, rect.width!),
        visible: message.visible === true,
      });
      return;
    }
    if (message?.type === WORKBENCH_DECISION_CHANGE_MESSAGE) {
      if (this.document()?.workbench.decision?.state === 'succeeded') return;
      const normalized = normalizeWorkbenchDecisionResponses(
        message.responses,
        this.decisionMarkup().points,
      );
      if (normalized) {
        this.decisionResponses.set(normalized);
        this.decisionDrafts.saveResponses(this.projectName(), this.workbenchId(), normalized);
      }
      return;
    }
    if (message?.type !== ISOLATED_HTML_LINK_MESSAGE || typeof message.href !== 'string') return;
    const entryPath = this.document()?.workbench.entryPath;
    if (!entryPath) return;
    const navigation = resolveIsolatedHtmlNavigation(entryPath, message.href);
    if (navigation?.kind === 'wiki') {
      this.openWiki.emit(navigation.relPath);
    } else if (navigation?.kind === 'external') {
      window.open(navigation.url, '_blank', 'noopener,noreferrer');
    }
  }

  toggleMaximize(): void {
    this.maximized.update((value) => !value);
  }

  @HostListener('document:keydown.escape')
  exitMaximized(): void {
    if (this.maximized()) this.maximized.set(false);
  }

  refreshDecision(): void {
    this.loadDocument(this.projectName(), this.workbenchId(), false);
  }

  refreshManually(): void {
    if (this.hub.connected()) return;
    this.loadDocument(this.projectName(), this.workbenchId(), false);
  }

  discardDecisionDraft(): void {
    const document = this.document();
    if (!document) return;
    this.decisionDrafts.discard(this.projectName(), this.workbenchId());
    const discovered = discoverWorkbenchDecisionMarkup(document.html);
    this.decisionResponses.set(document.workbench.decision?.responses ?? discovered.responses);
    this.hydrateFrame();
  }

  documentationReady(): boolean {
    const workbench = this.document()?.workbench;
    return workbench?.status === 'decided' && workbench.documentation?.eligible === true;
  }

  documentCurrent(): void {
    if (this.readOnly()) return;
    const document = this.document();
    if (!document || !this.documentationReady() || this.documenting()) return;
    this.documenting.set(true);
    this.documentationError.set(null);
    const project = encodeURIComponent(this.projectName());
    const id = encodeURIComponent(document.workbench.id);
    this.http.post<DocumentWorkbenchResult>(`/api/projects/${project}/workbenches/${id}/document`, {
        actor: 'Operator',
        expectedRevision: document.revision,
        expectedFingerprint: document.fingerprint,
      }).subscribe({
      next: () => {
        this.documenting.set(false);
        this.loadDocument(this.projectName(), this.workbenchId(), false);
      },
      error: error => {
        this.documenting.set(false);
        this.documentationError.set(error?.error?.error || 'The lifecycle could not be updated.');
      },
    });
  }

  onFrameLoaded(): void {
    this.hydrateFrame();
  }

  private hydrateFrame(): void {
    const decision = this.document()?.workbench.decision;
    this.frame()?.nativeElement.contentWindow?.postMessage(
      {
        type: WORKBENCH_DECISION_HYDRATE_MESSAGE,
        responses: this.decisionResponses(),
        readonly: this.readOnly() || decision?.state === 'succeeded',
      },
      '*',
    );
  }

  private loadDocument(project: string, id: string, clear = true): void {
    const generation = ++this.requestGeneration;
    this.loading.set(true);
    this.error.set(null);
    this.documentationError.set(null);
    if (clear) {
      this.document.set(null);
      this.decisionAnchor.set(null);
      this.lastUpdatedAtUtc.set(null);
    }
    this.docs.getWorkbench(project, id).subscribe({
      next: (document) => {
        if (generation !== this.requestGeneration) return;
        this.document.set(document);
        this.documentResolved.emit(document);
        this.lastUpdatedAtUtc.set(new Date().toISOString());
        const discovered = discoverWorkbenchDecisionMarkup(document.html);
        if (document.workbench.decision?.state === 'succeeded')
          this.decisionDrafts.discard(project, id);
        if (reworkRevisionLanded(document)) this.decisionDrafts.discard(project, id);
        const saved = this.decisionDrafts.draft(project, id)?.responses;
        const restored = saved
          ? normalizeWorkbenchDecisionResponses(saved, discovered.points)
          : null;
        this.decisionResponses.set(
          reworkRevisionLanded(document)
            ? discovered.responses
            : document.workbench.decision?.responses ?? restored ?? discovered.responses,
        );
        this.loading.set(false);
      },
      error: response => {
        if (generation !== this.requestGeneration) return;
        this.error.set(workbenchLoadError(response));
        this.loading.set(false);
      },
    });
  }
}

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function reworkRevisionLanded(document: WorkbenchDocument): boolean {
  const decision = document.workbench.decision;
  return decision?.outcome === 'rework'
    && Boolean(decision.sourceEntryFingerprint)
    && Boolean(document.entryFingerprint)
    && decision.sourceEntryFingerprint !== document.entryFingerprint;
}

/** `entryPath` is repo-root relative (`docs/...`); the asset resolver wants it docs-root relative. */
function workbenchDocRelPath(entryPath: string | undefined): string | null {
  if (!entryPath?.startsWith('docs/')) return null;
  return entryPath.slice('docs/'.length);
}

function workbenchLoadError(response: unknown): string {
  const candidate = response as {
    error?: unknown;
  } | null;
  const payload = candidate?.error;
  const reason = typeof payload === 'string'
    ? payload.trim()
    : typeof payload === 'object' && payload !== null && 'error' in payload
      ? String((payload as { error?: unknown }).error ?? '').trim()
      : '';
  return reason
    ? `Dossier could not be loaded: ${reason}`
    : 'Dossier could not be loaded. The server did not provide a reason.';
}
