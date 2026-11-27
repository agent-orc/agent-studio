import {
  ChangeDetectionStrategy, Component, ElementRef, HostListener, OnDestroy,
  computed, effect, inject, input, output, signal, untracked, viewChild,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import {
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  DocumentWorkbenchResult,
} from '../../../../models/project-docs.model';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { WorkbenchViewerHeaderComponent } from '../workbench-viewer-header/workbench-viewer-header.component';
import {
  ISOLATED_HTML_LINK_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  WORKBENCH_DECISION_READY_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  openIsolatedHtmlExternalLink,
  resolveIsolatedHtmlNavigation,
} from '../../../../services/sandboxed-html.util';
import { resolveWikiImageSrc } from '../../../../services/wiki-image-resolver';
import {
  discoverWorkbenchDecisionMarkup,
  normalizeWorkbenchDecisionResponses,
} from '../../../../services/workbench-decision-markup.util';
import { WorkbenchDecisionDraftStore } from '../../state/workbench-decision-draft.store';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { ConfirmDialogService } from '@studio/ui/services/confirm-dialog.service';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import { tabbableIn } from '../workbench-decision-panel/workbench-decision-focus.util';
import { carryRecordedSelections, hasUnsavedAnswers } from '../workbench-decision-panel/workbench-decision-panel.util';
import { DossierCardListComponent } from '../dossier-card-list/dossier-card-list.component';
import { ConceptPromotionComponent } from '../../../../components/concept-promotion/concept-promotion.component';
import { ActionPanelCoordinator } from '../../../../services/action-panel-coordinator.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { DossierPromotionNoticeComponent } from '../dossier-promotion-notice/dossier-promotion-notice.component';
import { ThemeService } from '../../../studio-shell/services/theme.service';

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
    DossierCardListComponent,
    ConceptPromotionComponent,
    DossierPromotionNoticeComponent,
  ],
  // One open action panel per Dossier view (AGT-3875).
  providers: [ActionPanelCoordinator],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer.component.html',
  styleUrl: './workbench-viewer.component.scss',
})
export class WorkbenchViewerComponent implements OnDestroy {
  readonly projectName = input.required<string>();
  readonly workbenchId = input.required<string>();
  readonly openWiki = output<{ relPath: string; reuse: 'replace-current' | 'new' }>();
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
  readonly theme = inject(ThemeService).theme;
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly notifications = inject(NotificationService);
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
  /** Timestamp of the last successful Dossier read, used by the offline as-of line. */
  readonly lastUpdatedAtUtc = signal<string | null>(null);
  readonly liveConnected = this.hub.connected;
  readonly connectionChangedAtUtc = this.hub.connectionChangedAtUtc;
  readonly readOnly = this.publicDemo.readOnly;
  /**
   * The concept card that published this Dossier, when the server says its
   * promotion can work. Its promotion creates one card per slice (AGT-3701).
   */
  readonly promotionSource = computed(() => {
    const promotion = this.document()?.promotion;
    if (this.readOnly() || !promotion?.available) return null;
    return promotion.sourceTaskKey;
  });
  /** Why a Dossier that proposes slices cannot promote them, with its next step (AGT-3875). */
  readonly promotionBlocked = computed(() => {
    const promotion = this.document()?.promotion;
    const noSlices = (this.document()?.cards?.implementationSlices ?? 0) === 0;
    return !promotion || promotion.available || (promotion.reason === 'no-proposals' && noSlices) ? null : promotion;
  });
  /** Answers changed in this browser that the Dossier does not hold yet (AGT-3875). */
  readonly unsavedAnswers = computed(() => !this.readOnly() && this.document() !== null && hasUnsavedAnswers(
    this.document()!, this.decisionDrafts.draft(this.projectName(), this.document()!.workbench.id)?.responses));
  private viewedDossier: { project: string; id: string; title: string } | null = null;

  readonly srcdoc = computed(() => {
    const document = this.document();
    const project = this.projectName();
    const docRelPath = workbenchDocRelPath(document?.workbench.entryPath);
    return buildIsolatedHtmlSrcdoc(document?.html ?? '', {
      workbenchDecisions: true,
      documentPattern: document?.workbench.pattern === 'ui' ? 'ui' : 'concept',
      colorScheme: this.theme(),
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
      const theme = this.theme();
      // This is the single audited HTML sink. The fixed wrapper is assigned as
      // a native string so Angular cannot remove the artifact's inline scripts.
      if (frame) {
        frame.nativeElement.style.colorScheme = theme;
        frame.nativeElement.srcdoc = this.srcdoc();
      }
    });
    effect(() => {
      const project = this.projectName();
      const id = this.workbenchId();
      untracked(() => this.warnUnsavedOnLeave());
      this.maximized.set(false);
      this.loadDocument(project, id);
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
      button?: unknown;
      ctrlKey?: unknown;
      metaKey?: unknown;
      responses?: unknown;
    } | null;
    if (message?.type === WORKBENCH_DECISION_READY_MESSAGE) {
      this.hydrateFrame();
      return;
    }
    if (message?.type === WORKBENCH_DECISION_FOCUS_ACTION_MESSAGE) {
      this.focusPastFrame();
      return;
    }
    if (message?.type === WORKBENCH_DECISION_CHANGE_MESSAGE) {
      if (this.document()?.workbench.decision?.state === 'succeeded'
        || this.document()?.workbench.status === 'decided') return;
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
      this.openWiki.emit({
        relPath: navigation.relPath,
        reuse: message.button === 1 || message.ctrlKey === true || message.metaKey === true
          ? 'new'
          : 'replace-current',
      });
    } else if (navigation?.kind === 'external') {
      void openIsolatedHtmlExternalLink(this.confirmDialog, navigation.url);
    }
  }

  /** Closing or reloading the browser tab with unsaved answers asks first. */
  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(event: BeforeUnloadEvent): void {
    if (!this.unsavedAnswers()) return;
    event.preventDefault();
    event.returnValue = '';
  }

  ngOnDestroy(): void {
    this.warnUnsavedOnLeave();
  }

  /**
   * Leaving a Dossier with unsaved answers warns once per unsaved state: the
   * answers stay as a draft in this browser, but the Dossier does not hold them.
   */
  private warnUnsavedOnLeave(): void {
    const viewed = this.viewedDossier;
    this.viewedDossier = null;
    const document = this.document();
    const draft = viewed && this.decisionDrafts.draft(viewed.project, viewed.id);
    if (!viewed || !draft || draft.leaveWarned || document?.workbench.id !== viewed.id || this.readOnly()
      || !hasUnsavedAnswers(document, draft.responses)) return;
    this.decisionDrafts.markLeaveWarned(viewed.project, viewed.id);
    this.notifications.warning(`Your answers in ${viewed.title} are not saved in the Dossier. They stay as a `
      + 'draft in this browser; open the Dossier and choose Save answers.', 'Answers not saved');
  }

  toggleMaximize(): void {
    this.maximized.update((value) => !value);
  }

  containDecisionScrollKeys(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target.closest('input, textarea, select, [contenteditable="true"], [role="listbox"], [role="combobox"], [role="menu"]')) return;
    const panel = event.currentTarget as HTMLElement;
    const moves: Record<string, number> = {
      ArrowUp: -48,
      ArrowDown: 48,
      PageUp: -panel.clientHeight,
      PageDown: panel.clientHeight,
      Home: -panel.scrollHeight,
      End: panel.scrollHeight,
    };
    if (!(event.key in moves)) return;
    event.preventDefault();
    event.stopPropagation();
    panel.scrollBy({ top: moves[event.key] });
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
    this.decisionResponses.set(initialDecisionResponses(document, null));
    this.hydrateFrame();
  }

  documentationReady(): boolean {
    const workbench = this.document()?.workbench;
    return workbench?.status === 'decided' && workbench.documentation?.eligible === true;
  }

  /** Why a decided item cannot be marked documented yet, for example open implementation slices. */
  documentationBlockedReason(): string | null {
    const workbench = this.document()?.workbench;
    if (workbench?.status !== 'decided' || workbench.documentation?.eligible !== false) return null;
    return workbench.documentation.blockedReason || null;
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

  /**
   * The artifact cancels Tab on its last decision control and asks the host to
   * move focus. Focus must always leave the frame (AGT-3829): to the first
   * enabled decision-bar control, else to the next tabbable host element. A
   * disabled or removed target would leave focus trapped in the frame.
   */
  private focusPastFrame(): void {
    const frame = this.frame()?.nativeElement;
    if (!frame) return;
    const host = this.inlineActionHost()?.nativeElement;
    const target = (host ? tabbableIn(host)[0] : undefined)
      ?? tabbableIn(frame.ownerDocument.body).find(element =>
        (frame.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);
    target?.focus();
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
        readonly: this.readOnly() || decision?.state === 'succeeded'
          || this.document()?.workbench.status === 'decided',
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
      this.lastUpdatedAtUtc.set(null);
    }
    this.docs.getWorkbench(project, id).subscribe({
      next: (document) => {
        if (generation !== this.requestGeneration) return;
        this.document.set(document);
        this.viewedDossier = { project, id, title: document.workbench.title };
        this.documentResolved.emit(document);
        this.lastUpdatedAtUtc.set(new Date().toISOString());
        if (document.workbench.decision?.state === 'succeeded')
          this.decisionDrafts.discard(project, id);
        if (reworkRevisionSettled(document)) this.decisionDrafts.discard(project, id);
        this.decisionResponses.set(initialDecisionResponses(
          document, this.decisionDrafts.draft(project, id)?.responses ?? null));
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

/**
 * The responses the Dossier opens with. A landed revision shows the owner's
 * recorded selections from the rework receipt, not the authored defaults
 * (AGT-3703); otherwise the receipt, then the browser draft, then the saved
 * answers, then the markup.
 */
function initialDecisionResponses(
  document: WorkbenchDocument,
  saved: readonly WorkbenchDecisionResponse[] | null,
): WorkbenchDecisionResponse[] {
  const discovered = discoverWorkbenchDecisionMarkup(document.html);
  const decision = document.workbench.decision;
  if (reworkRevisionSettled(document))
    return carryRecordedSelections(decision?.responses ?? document.workbench.recordedResponses,
      discovered.points, discovered.responses);
  const restored = saved ? normalizeWorkbenchDecisionResponses(saved, discovered.points) : null;
  // A saved answer set discards the draft, so a draft is newer than the saved answers (AGT-3875).
  const recorded = document.workbench.recordedResponses?.length ? document.workbench.recordedResponses : null;
  return decision?.responses ?? restored ?? recorded ?? discovered.responses;
}

function reworkRevisionLanded(document: WorkbenchDocument): boolean {
  const decision = document.workbench.decision;
  return decision?.outcome === 'rework'
    && Boolean(decision.sourceEntryFingerprint)
    && Boolean(document.entryFingerprint)
    && decision.sourceEntryFingerprint !== document.entryFingerprint;
}

function reworkRevisionSettled(document: WorkbenchDocument): boolean {
  return reworkRevisionLanded(document)
    || document.revisionRound?.status === 'updated'
    || document.revisionRound?.status === 'unchanged'
    || document.revisionRound?.status === 'failed';
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
