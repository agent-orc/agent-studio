import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { TaskService } from '../../../../../services/task.service';
import { MarkdownRichEditorComponent } from '../../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import { FileSourceHistoryComponent } from '../../../../../components/file-source-history/file-source-history.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskArtifact, TaskArtifactKind } from '../../../../../models/task.model';
import { AspectJsonCardComponent } from './aspect-json-card/aspect-json-card.component';
import { parseAspectDocument, type AspectDocument } from './aspect-document.model';
import { cleanStepResultMarkdown } from '../pipeline-step-result/pipeline-step-result.util';
import {
  compareDocuments,
  documentAnchor,
  isResultDocument,
  presentDocument,
  type DocumentPresentation,
} from './document-presentation.util';
import { DocumentDetailsMenuComponent } from './document-details-menu/document-details-menu.component';
import { ArchivedFilesManifestComponent } from '../../../../retention/components/archived-files-manifest/archived-files-manifest.component';

/**
 * Docs tab body. Renders supported documents directly in the job folder
 * (prompt + aspect verdicts + operator notes + HTML explorations) as a list of
 * cards. Outcome documents lead; prompts and raw artifacts remain available
 * afterwards.
 *
 * Expand / collapse rules (F48):
 *   - Result documents start expanded so their rendered conclusions are
 *     immediately visible; prompts and raw artifacts start as previews.
 *   - Polling may replace the artifact objects or add files without changing
 *     expansion state. State resets only when a different task is opened.
 *
 * Editing rule: only the prompt card is editable. The card flips from
 * the rendered markdown view to {@link MarkdownRichEditorComponent} when
 * the user clicks Edit, and back on save. Aspect / note / other files
 * stay read-only in this scope.
 *
 * Content fetch: the artifact manifest already contains size + mtime
 * but no body. Content is fetched lazily per file through
 * `TaskService.readJobFile` and cached in {@link fileContent}; the prompt
 * body is supplied by the parent (it's already part of `TaskDetail`).
 */
@Component({
  selector: 'app-files-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FileSourceHistoryComponent, MarkdownRichEditorComponent, MarkdownViewComponent, TooltipDirective, AspectJsonCardComponent, DocumentDetailsMenuComponent, ArchivedFilesManifestComponent],
  templateUrl: './files-pane.component.html',
  styleUrl: './files-pane.component.scss',
})
export class FilesPaneComponent {
  private readonly jobs = inject(TaskService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly artifacts = input<TaskArtifact[]>([]);
  /** Prefilled body for `prompt.md` so we don't re-fetch what `TaskDetail` already loaded. */
  readonly promptContent = input<string>('');
  readonly jobId = input<string | null>(null);
  readonly archived = input(false);
  readonly watchPath = input<string | null>(null);
  readonly isRunning = input(false);
  readonly focusRequest = input<{ kind: TaskArtifactKind; requestId: number } | null>(null);

  readonly save = output<string>();

  /** File names whose card is currently expanded. */
  private readonly expanded = signal<Set<string>>(new Set());
  /** Cached file bodies. `null` marks a load error so the view can render a tidy fallback. */
  private readonly content = signal<Map<string, string | null>>(new Map());
  private readonly loading = signal<Set<string>>(new Set());
  private readonly rawVisible = signal<Set<string>>(new Set());
  private readonly historyVisible = signal<Set<string>>(new Set());
  private readonly editingPrompt = signal(false);
  /** Tracks which file names we have already kicked off a fetch for, to avoid duplicate calls. */
  private readonly fetched = new Set<string>();
  private readonly initialized = new Set<string>();
  /**
   * Memoised parse of structured `aspect-*.json` bodies, keyed by file name.
   * Re-parsed only when the cached raw body changes, so the OnPush template
   * can call {@link aspectDoc} per change-detection without re-running
   * `JSON.parse` every cycle.
   */
  private readonly aspectDocCache = new Map<string, { raw: string; doc: AspectDocument | null }>();
  private readonly htmlDocCache = new Map<string, { raw: string; doc: SafeHtml }>();
  private readonly presentationCache = new Map<string, { raw: string; value: DocumentPresentation }>();

  readonly orderedArtifacts = computed(() => [...this.artifacts()].sort(compareDocuments));

  readonly onlyPrompt = computed(() => {
    const list = this.artifacts();
    return list.length === 1 && list[0].kind === 'prompt';
  });

  constructor() {
    // Expansion is user-owned UI state. Artifact polling replaces the input
    // array every 10 seconds, so reset only at the task boundary.
    effect(() => {
      this.jobId();
      this.expanded.set(new Set());
      this.editingPrompt.set(false);
      this.content.set(new Map());
      this.loading.set(new Set());
      this.rawVisible.set(new Set());
      this.historyVisible.set(new Set());
      this.fetched.clear();
      this.initialized.clear();
      this.aspectDocCache.clear();
      this.htmlDocCache.clear();
      this.presentationCache.clear();
    }, { allowSignalWrites: true });

    // Outcome documents open on arrival. Polling may add more, but a document
    // the operator deliberately collapsed stays collapsed.
    effect(() => {
      const nextExpanded = new Set(this.expanded());
      let changed = false;
      for (const file of this.orderedArtifacts()) {
        if (this.initialized.has(file.name)) continue;
        this.initialized.add(file.name);
        if (isResultDocument(file)) {
          nextExpanded.add(file.name);
          changed = true;
        }
      }
      if (changed) this.expanded.set(nextExpanded);
    }, { allowSignalWrites: true });

    // Prefetch content for every non-prompt artifact so previews / expansions
    // are instant. Prompt body is supplied by the parent — no fetch needed.
    effect(() => {
      const list = this.artifacts();
      const jobId = this.jobId();
      const watchPath = this.watchPath() ?? undefined;
      if (!jobId) return;
      for (const a of list) {
        if (a.kind === 'prompt') continue;
        if (this.fetched.has(a.name)) continue;
        this.fetched.add(a.name);
        this.markLoading(a.name, true);
        this.jobs.readJobFile(jobId, a.name, watchPath).subscribe({
          next: (text) => {
            this.setContent(a.name, typeof text === 'string' ? text : '');
            this.markLoading(a.name, false);
          },
          error: () => {
            this.setContent(a.name, null);
            this.markLoading(a.name, false);
          },
        });
      }
    });

    effect(() => {
      const request = this.focusRequest();
      if (!request) return;
      const target = this.orderedArtifacts().find((file) => file.kind === request.kind);
      if (target) this.focusDocument(target);
    }, { allowSignalWrites: true });
  }

  isExpanded(name: string): boolean {
    return this.expanded().has(name);
  }

  toggleExpanded(name: string): void {
    const next = new Set(this.expanded());
    if (next.has(name)) {
      next.delete(name);
      if (this.isPromptFile(name)) this.editingPrompt.set(false);
    } else {
      next.add(name);
    }
    this.expanded.set(next);
  }

  toggleFromHeader(file: TaskArtifact, event: Event): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('button, summary, app-document-details-menu')) return;
    if (event instanceof KeyboardEvent && event.key === ' ') event.preventDefault();
    this.toggleExpanded(file.name);
  }

  /** Resolves the body for a file. Prompt comes from the input; others come from the cache. */
  bodyFor(file: TaskArtifact): string | null | undefined {
    if (file.kind === 'prompt') return this.promptContent();
    return this.content().get(file.name);
  }

  presentationFor(file: TaskArtifact): DocumentPresentation {
    const raw = this.bodyFor(file) || '';
    const cached = this.presentationCache.get(file.name);
    if (cached?.raw === raw) return cached.value;
    const value = presentDocument(file, raw, this.aspectDoc(file));
    this.presentationCache.set(file.name, { raw, value });
    return value;
  }

  anchorFor(file: TaskArtifact): string {
    return documentAnchor(file);
  }

  focusDocument(file: TaskArtifact): void {
    if (!this.isExpanded(file.name)) {
      const next = new Set(this.expanded());
      next.add(file.name);
      this.expanded.set(next);
    }
    setTimeout(() => {
      const target = this.host.nativeElement.querySelector<HTMLElement>(`#${documentAnchor(file)}`);
      target?.scrollIntoView({ block: 'start' });
      target?.focus({ preventScroll: true });
    });
  }

  isLoading(name: string): boolean {
    return this.loading().has(name);
  }

  isRawVisible(name: string): boolean {
    return this.rawVisible().has(name);
  }

  isHistoryVisible(name: string): boolean {
    return this.historyVisible().has(name);
  }

  toggleRaw(file: TaskArtifact): void {
    const next = new Set(this.rawVisible());
    if (next.has(file.name)) next.delete(file.name);
    else next.add(file.name);
    this.rawVisible.set(next);
    const nextHistory = new Set(this.historyVisible());
    nextHistory.delete(file.name);
    this.historyVisible.set(nextHistory);
    if (!this.isExpanded(file.name)) this.focusDocument(file);
  }

  toggleHistory(file: TaskArtifact): void {
    const next = new Set(this.historyVisible());
    if (next.has(file.name)) next.delete(file.name);
    else next.add(file.name);
    this.historyVisible.set(next);
    const nextRaw = new Set(this.rawVisible());
    nextRaw.delete(file.name);
    this.rawVisible.set(nextRaw);
    if (!this.isExpanded(file.name)) this.focusDocument(file);
  }

  /** True for a structured `aspect-*.json` artefact (rendered as a card). */
  isAspectJson(file: TaskArtifact): boolean {
    return file.kind === 'aspect' && file.name.toLowerCase().endsWith('.json');
  }

  isHtmlFile(file: TaskArtifact): boolean {
    return /\.html?$/i.test(file.name);
  }

  /**
   * `allow-scripts` powers self-contained interaction in the template iframe.
   * `allow-same-origin` is deliberately omitted so the document receives an
   * opaque origin and cannot inherit Studio's origin or directly read its
   * cookies, storage, or DOM. Network requests still follow normal browser
   * and CORS policy.
   */
  trustedHtmlFor(file: TaskArtifact): SafeHtml | null {
    if (!this.isHtmlFile(file)) return null;
    const raw = this.bodyFor(file);
    if (raw == null) return null;
    const cached = this.htmlDocCache.get(file.name);
    if (cached && cached.raw === raw) return cached.doc;
    const doc = this.sanitizer.bypassSecurityTrustHtml(raw);
    this.htmlDocCache.set(file.name, { raw, doc });
    return doc;
  }

  /**
   * Parsed structured aspect document for an `aspect-*.json` file, or `null`
   * when its body has not loaded yet or is not a valid aspect document (in
   * which case the caller falls back to the markdown renderer). Memoised on
   * the raw body so repeat calls during change detection are cheap.
   */
  aspectDoc(file: TaskArtifact): AspectDocument | null {
    if (!this.isAspectJson(file)) return null;
    const raw = this.bodyFor(file);
    if (raw == null) return null;
    const cached = this.aspectDocCache.get(file.name);
    if (cached && cached.raw === raw) return cached.doc;
    const doc = parseAspectDocument(raw);
    this.aspectDocCache.set(file.name, { raw, doc });
    return doc;
  }

  /** Renders the first ~12 lines of a file's content for the preview block. */
  preview(file: TaskArtifact): string | null {
    const body = this.presentationFor(file).body;
    if (body == null) return null;
    return this.generatePreview(body);
  }

  private generatePreview(content: string): string {
    const lines = content.split(/\r?\n/).slice(0, 12);
    return lines
      .map((l) => (l.length > 120 ? l.slice(0, 117) + '…' : l))
      .join('\n');
  }

  fileIcon(kind: TaskArtifactKind): string {
    switch (kind) {
      case 'prompt': return '\u{1F4DD}'; // memo
      case 'aspect': return '\u{1F50D}'; // magnifying-glass
      case 'codeReview': return 'CR';
      case 'note':   return '\u{1F4CC}'; // pushpin
      default:       return '\u{1F4C4}'; // page-facing-up
    }
  }

  readonly documentBodyTransform = cleanStepResultMarkdown;

  isPromptFile(name: string): boolean {
    return name === 'prompt.md';
  }

  isEditing(): boolean { return this.editingPrompt(); }

  beginEditPrompt(event: Event): void {
    event.stopPropagation();
    if (this.isRunning()) return;
    this.editingPrompt.set(true);
  }

  cancelEditPrompt(event: Event): void {
    event.stopPropagation();
    this.editingPrompt.set(false);
  }

  onPromptSave(content: string): void {
    this.save.emit(content);
    this.editingPrompt.set(false);
  }

  private setContent(name: string, value: string | null): void {
    const next = new Map(this.content());
    next.set(name, value);
    this.content.set(next);
  }

  private markLoading(name: string, on: boolean): void {
    const next = new Set(this.loading());
    if (on) next.add(name);
    else next.delete(name);
    this.loading.set(next);
  }
}
