import { AfterViewInit, ChangeDetectionStrategy, Component, DestroyRef, ElementRef, ViewChild, computed, inject, input, model, output, signal } from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { FormsModule } from '@angular/forms';
import { TaskState, type CliType, type ComponentRoutingResolution, type TagRegistryEntry, type TaskKind, type TaskMode, type WatchPathEntry } from '../../../../models/task.model';
import type { CliModelInfo } from '../../../../features/cli';
import { TaskService } from '../../../../services/task.service';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { CreateEpicPickerComponent } from '../create-epic-picker/create-epic-picker.component';
import { CreateModePickerComponent } from '../create-mode-picker/create-mode-picker.component';
import { RoutingPreviewComponent } from '../routing-preview/routing-preview.component';
import type { ModelRoutingRecommendation } from '../../../quota';
import { ModelRoutingSuggestionComponent } from '../model-routing-suggestion/model-routing-suggestion.component';
import { lanePresentation } from '../../../../models/lane-presentation';
export interface PendingAttachment {
  id: string;
  file: File;
  alt: string;
  previewUrl: string;
}
export interface LaneOption {
  state: string;
  label: string;
  icon: string;
}
/** Manual create targets stop before orchestrator-owned Progress. */
export const CREATE_LANE_OPTIONS: readonly LaneOption[] = [
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.Ready,
].map((state) => {
  const lane = lanePresentation(state);
  return { state, label: lane.name, icon: lane.glyph };
});

const PENDING_PREFIX = 'pending-attachment-';

/**
 * "Create task" dialog. The parent owns draft state; this component
 * renders the form and captures pasted/dropped images (upload happens
 * after the job folder is created), and emits intent (cancel /
 * submit / cliType change). Two-way bindings via `model()` keep
 * title / watchPath / model / prompt / attachments / target lane /
 * tags / type in sync with the parent.
 *
 * The "Enhance" button is the primary entry path: it calls
 * /api/prompt/enhance and /api/title/generate in parallel, then writes
 * the refined prompt, generated title, and any tags that resolve in the
 * workspace registry directly into the bound fields. There is no
 * preview / Apply / Discard step - the fields are the preview. The
 * user can still edit anything before submitting.
 */
@Component({
  selector: 'app-create-job-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TooltipDirective, CliModelSelectorComponent, CreateEpicPickerComponent, CreateModePickerComponent, RoutingPreviewComponent, ModelRoutingSuggestionComponent],
  templateUrl: './create-task-dialog.component.html',
  styleUrl: './create-task-dialog.component.scss'
})
export class CreateTaskDialogComponent implements AfterViewInit {
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly availableModels = input<CliModelInfo[]>([]);
  readonly cliTypeDraft = input.required<CliType>();
  readonly routing = input<ComponentRoutingResolution | null>(null); readonly routingPending = input(false);
  readonly policySuggestion = input<ModelRoutingRecommendation | null>(null); readonly modelSelectionExplicit = input(false);
  readonly newTitle = model<string>('');
  readonly newWatchPath = model<string>('');
  readonly newModel = model<string>('');
  readonly newThinkingLevel = model<string | null>(null);
  readonly newPrompt = model<string>('');
  readonly attachments = model<PendingAttachment[]>([]);
  /** Backlog-lane spec: structural classification picker. */
  readonly newTaskType = model<string>('chore');
  /** Backlog-lane spec: tag ids attached on create. */
  readonly newTags = model<string[]>([]);
  /** Lane the new task lands in. One of the entries in CREATE_LANE_OPTIONS. */
  readonly newTargetState = model<string>(TaskState.Preparation);
  /** Card kind: `task` (default) or `epic` (a sub-task container). */
  readonly newKind = model<TaskKind>('task');
  readonly isEpic = computed(() => this.newKind() === 'epic');
  /** Way 1: parent epic id for a `kind=task`. Hidden when kind=epic. */
  readonly newEpicId = model<string>('');
  readonly newMode = model<TaskMode>('coding');
  readonly newAllowWebAccess = model<boolean>(false);

  setKind(kind: TaskKind): void { this.newKind.set(kind); }

  readonly tagRegistryStore = inject(TagRegistryStore);
  readonly availableTags = computed(() => this.tagRegistryStore.tags());

  readonly laneOptions = CREATE_LANE_OPTIONS;
  /** Reactive dialog header derived from the chosen target lane. */
  readonly header = computed<string>(() => {
    switch (this.newTargetState()) {
      case TaskState.Backlog:     return 'Add to Backlog';
      case TaskState.Preparation: return 'New Preparation Task';
      case TaskState.Ready:       return 'New Ready Task';
      default:              return 'New Task';
    }
  });

  toggleTag(id: string): void {
    const current = this.newTags();
    const next = current.includes(id) ? current.filter(t => t !== id) : [...current, id];
    this.newTags.set(next);
  }

  setTargetState(state: string): void {
    this.newTargetState.set(state);
  }
  readonly cliTypeChange = output<CliType>();
  readonly modelSelectionTouched = output<void>(); readonly policySelectionRequest = output<void>();
  readonly cancelRequest = output<void>();
  readonly openChatRequest = output<void>();
  readonly submitRequest = output<void>();

  readonly isDragging = model<boolean>(false);
  readonly attachmentError = model<string | null>(null);

  private readonly jobs = inject(TaskService);
  readonly titleGenerating = signal(false);
  readonly titleGenerateError = signal<string | null>(null);

  readonly canGenerateTitle = computed(() => {
    const prompt = (this.newPrompt() ?? '').trim();
    return prompt.length > 0 && !this.titleGenerating();
  });

  /**
   * In-flight state for the unified Enhance action. Drives the
   * "Enhancing..." spinner and disables the button to prevent
   * double-submits.
   */
  readonly enhancing = signal(false);
  readonly enhanceError = signal<string | null>(null);
  /**
   * Short status line shown after a successful Enhance summarising what
   * the user got applied (intent + matched/unmatched tag breakdown).
   * Cleared as soon as the user starts editing again.
   */
  readonly enhanceSummary = signal<{ intent: string; appliedTagIds: string[]; unknownTags: string[] } | null>(null);

  readonly canEnhance = computed(() => {
    const prompt = (this.newPrompt() ?? '').trim();
    return prompt.length > 0 && !this.enhancing();
  });

  readonly hasAttachments = computed(() => this.attachments().length > 0);

  @ViewChild('promptArea') private promptArea?: ElementRef<HTMLTextAreaElement>;
  @ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    // The dialog is mounted only while open (parent renders it under
    // @if (showCreate())), so a one-shot push-on-construct keeps the
    // stack ordering honest without touching the parent.
    this.modalStack.pushUntilDestroyed(
      'create-job-dialog',
      () => this.cancelRequest.emit(),
      this.destroyRef,
    );
  }

  ngAfterViewInit(): void {
    // Focus lands in the prompt textarea so Ctrl+V immediately captures
    // a clipboard screenshot via the wrapper's (paste) handler. Without
    // this, the user has to click into the dialog first - the dialog
    // hint says "paste with Ctrl+V" so silent paste failure is the
    // reported bug.
    queueMicrotask(() => this.promptArea?.nativeElement?.focus());
  }

  triggerFilePicker(): void {
    this.fileInput?.nativeElement.click();
  }

  generateTitle(): void {
    if (!this.canGenerateTitle()) return;
    const prompt = (this.newPrompt() ?? '').trim();
    if (prompt.length === 0) return;

    this.titleGenerating.set(true);
    this.titleGenerateError.set(null);
    this.jobs.generateTaskTitle(prompt).subscribe({
      next: (resp) => {
        const t = (resp?.title ?? '').trim();
        if (t.length > 0) this.newTitle.set(t);
        this.titleGenerating.set(false);
      },
      error: (err) => {
        const msg = err?.error?.error
          || err?.error?.detail
          || err?.message
          || 'Could not generate a title. Try again or type one.';
        this.titleGenerateError.set(msg);
        this.titleGenerating.set(false);
      }
    });
  }

  /**
   * One-click "fill every field from the prompt". Runs the prompt
   * enhancer and the title generator in parallel via Haiku, then writes
   * the refined prompt, the generated title, any tags that resolve to
   * known registry entries, and a sensible target lane back into the
   * bound fields. No preview - the fields are the preview. The user can
   * still edit before submitting.
   */
  enhancePrompt(): void {
    if (!this.canEnhance()) return;
    const prompt = (this.newPrompt() ?? '').trim();
    if (prompt.length === 0) return;

    this.enhancing.set(true);
    this.enhanceError.set(null);
    this.enhanceSummary.set(null);

    const enhance$ = this.jobs.enhancePrompt(prompt).pipe(
      map((r) => ({ ok: true as const, value: r })),
      catchError((err: unknown) => of({ ok: false as const, error: err }))
    );
    const title$ = this.jobs.generateTaskTitle(prompt).pipe(
      map((r) => ({ ok: true as const, value: r })),
      catchError((err: unknown) => of({ ok: false as const, error: err }))
    );

    forkJoin({ enhance: enhance$, title: title$ }).subscribe(({ enhance, title }) => {
      this.enhancing.set(false);

      if (!enhance.ok) {
        const err = enhance.error as { error?: { error?: string; detail?: string }; message?: string } | undefined;
        const msg = err?.error?.error
          || err?.error?.detail
          || err?.message
          || 'Could not enhance the prompt. Try again.';
        this.enhanceError.set(msg);
        return;
      }

      const refined = (enhance.value?.refinedPrompt ?? '').trim();
      const intent = (enhance.value?.intent ?? '').trim();
      const suggestedTags = Array.isArray(enhance.value?.tags)
        ? enhance.value.tags.filter((t) => !!t)
        : [];

      if (refined.length === 0 && intent.length === 0 && suggestedTags.length === 0) {
        this.enhanceError.set('Enhancer returned an empty result. Try again.');
        return;
      }

      if (refined.length > 0) this.newPrompt.set(refined);
      if (title.ok) {
        const t = (title.value?.title ?? '').trim();
        if (t.length > 0) this.newTitle.set(t);
      }

      const { appliedTagIds, unknownTags } = this.resolveTagSuggestions(suggestedTags);
      if (appliedTagIds.length > 0) {
        const current = new Set(this.newTags());
        for (const id of appliedTagIds) current.add(id);
        this.newTags.set([...current]);
      }

      // Enhanced + titled tasks tend to be actionable - default the lane
      // to Ready unless the user already moved it past Backlog. Don't
      // overwrite an explicit Backlog choice (the user wanted triage).
      const lane = this.newTargetState();
      if (lane === TaskState.Preparation) {
        this.newTargetState.set(TaskState.Ready);
      }

      this.enhanceSummary.set({ intent, appliedTagIds, unknownTags });
    });
  }

  /**
   * Match Haiku-suggested kebab-case tag tokens against the workspace
   * registry (id first, then case-insensitive label). Returns the
   * registry ids that resolved + the suggestion strings that didn't,
   * so the dialog can show a "suggested but not in registry" hint.
   */
  private resolveTagSuggestions(suggestions: readonly string[]): { appliedTagIds: string[]; unknownTags: string[] } {
    const registry = this.availableTags();
    const byId = new Map<string, TagRegistryEntry>();
    const byLabel = new Map<string, TagRegistryEntry>();
    for (const t of registry) {
      byId.set(t.id.toLowerCase(), t);
      byLabel.set(t.label.toLowerCase(), t);
    }
    const applied: string[] = [];
    const unknown: string[] = [];
    const seen = new Set<string>();
    for (const raw of suggestions) {
      const slug = (raw ?? '').trim().toLowerCase();
      if (slug.length === 0) continue;
      const hit = byId.get(slug) ?? byLabel.get(slug);
      if (hit) {
        if (!seen.has(hit.id)) {
          applied.push(hit.id);
          seen.add(hit.id);
        }
      } else {
        unknown.push(raw);
      }
    }
    return { appliedTagIds: applied, unknownTags: unknown };
  }

  clearEnhanceSummary(): void {
    this.enhanceSummary.set(null);
    this.enhanceError.set(null);
  }

  // Escape handling is delegated to ModalStackService (constructor below);
  // the dialog is mounted only while open so a simple push-on-construct /
  // dispose-on-destroy keeps the stack honest. See
  // services/modal-stack.service.ts.

  onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    for (const file of files) {
      if (file.type.startsWith('image/')) this.addAttachment(file);
    }
    input.value = '';
  }

  onPromptPaste(event: ClipboardEvent): void {
    const file = this.imageFromClipboard(event.clipboardData);
    if (!file) return;
    event.preventDefault();
    this.addAttachment(file);
  }

  onDragOver(event: DragEvent): void {
    if (!event.dataTransfer) return;
    if (!Array.from(event.dataTransfer.types).includes('Files')) return;
    event.preventDefault();
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent): void {
    if (event.target !== event.currentTarget) return;
    this.isDragging.set(false);
  }

  onDrop(event: DragEvent): void {
    this.isDragging.set(false);
    const files = Array.from(event.dataTransfer?.files ?? []).filter(f => f.type.startsWith('image/'));
    if (files.length === 0) return;
    event.preventDefault();
    for (const file of files) this.addAttachment(file);
  }

  removeAttachment(id: string): void {
    const list = this.attachments();
    const found = list.find(a => a.id === id);
    if (found) URL.revokeObjectURL(found.previewUrl);
    this.attachments.set(list.filter(a => a.id !== id));

    // Drop the placeholder line out of the prompt as well.
    const current = this.newPrompt() ?? '';
    const stripped = current.replace(
      new RegExp(`!\\[[^\\]]*\\]\\(${PENDING_PREFIX}${id}\\)\\n?`, 'g'),
      ''
    );
    if (stripped !== current) this.newPrompt.set(stripped);
  }

  private addAttachment(file: File): void {
    if (file.size > 10 * 1024 * 1024) {
      this.attachmentError.set('Image too large (max 10 MB).');
      return;
    }
    this.attachmentError.set(null);

    const id = this.makeId();
    const alt = this.deriveAlt(file);
    const previewUrl = URL.createObjectURL(file);
    const next: PendingAttachment = { id, file, alt, previewUrl };
    this.attachments.set([...this.attachments(), next]);

    this.insertPlaceholder(alt, id);
  }

  private insertPlaceholder(alt: string, id: string): void {
    const ref = `![${alt}](${PENDING_PREFIX}${id})`;
    const area = this.promptArea?.nativeElement;
    const current = this.newPrompt() ?? '';

    if (area && document.activeElement === area) {
      const start = area.selectionStart ?? current.length;
      const end = area.selectionEnd ?? current.length;
      const before = current.slice(0, start);
      const after = current.slice(end);
      const needsLeadingNl = before.length > 0 && !before.endsWith('\n') ? '\n' : '';
      const insert = `${needsLeadingNl}${ref}\n`;
      const next = before + insert + after;
      this.newPrompt.set(next);
      // Move caret after the inserted reference on the next tick.
      queueMicrotask(() => {
        const pos = (before + insert).length;
        area.setSelectionRange(pos, pos);
        area.focus();
      });
    } else {
      const sep = current.length === 0 || current.endsWith('\n') ? '' : '\n';
      this.newPrompt.set(current + sep + ref + '\n');
    }
  }

  private imageFromClipboard(data: DataTransfer | null): File | null {
    if (!data) return null;
    for (const item of Array.from(data.items)) {
      if (item.kind === 'file' && item.type.startsWith('image/')) {
        const file = item.getAsFile();
        if (file) return file;
      }
    }
    for (const file of Array.from(data.files ?? [])) {
      if (file.type.startsWith('image/')) return file;
    }
    return null;
  }

  private deriveAlt(file: File): string {
    const stem = (file.name ?? '').replace(/\.[^.]+$/, '').trim();
    return stem || 'screenshot';
  }

  private makeId(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID().replace(/-/g, '').slice(0, 12);
    }
    return Math.random().toString(36).slice(2, 14);
  }

  /** Resolve a tag id to its registry entry for the "applied tags" hint. */
  tagLabelFor(id: string): string {
    const tag = this.availableTags().find((t) => t.id === id);
    return tag?.label ?? id;
  }
}
