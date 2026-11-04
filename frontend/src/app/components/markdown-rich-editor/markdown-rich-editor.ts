import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, HostListener, OnDestroy, ViewChild, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { Editor } from '@tiptap/core';
import { htmlToMarkdown, markdownToHtml, MarkdownImageOptions } from 'coding-agent-chat/markdown';
import { shouldEmitEditorSave } from './markdown-rich-editor.guard';
import { sessionFetch } from '../../services/session-fetch';
import { MediaLightboxService } from '../../services/media-lightbox.service';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../menu';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';
import { DEFAULT_EDITOR_LOCK_REASON, editorSaveState, resolveEditorState, type EditorState } from './markdown-rich-editor-status';

const ATTACHMENTS_PREFIX = 'attachments/';

@Component({
  selector: 'app-markdown-rich-editor',
  standalone: true,
  imports: [FormsModule, TooltipDirective, MenuComponent, StatusIndicatorComponent],
  templateUrl: './markdown-rich-editor.html',
  styleUrl: './markdown-rich-editor.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MarkdownRichEditorComponent implements AfterViewInit, OnDestroy {
  readonly value = input('');
  readonly readOnly = input(false);
  readonly readOnlyReason = input<string | null>(null);
  readonly autoSave = input(true);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly save = output<string>();
  readonly sourceValue = signal('');
  readonly mode = signal<'rich' | 'source'>('rich');
  readonly savedAt = signal(0);
  readonly uploading = signal(false);
  readonly uploadError = signal<string | null>(null);
  readonly isDragging = signal(false);

  /** Overflow `…` menu that hosts the rich ↔ markdown-source toggle. */
  readonly modeMenuOpen = signal(false);
  readonly modeMenuItems = computed<MenuItem[]>(() => [
    { kind: 'row', id: 'rich',   label: 'View as rich text',       active: this.mode() === 'rich'   },
    { kind: 'row', id: 'source', label: 'View as Markdown source', active: this.mode() === 'source' },
  ]);
  toggleModeMenu(): void { this.modeMenuOpen.update(v => !v); }
  onModeMenuClick(ev: MenuItemClickEvent): void {
    if (ev.id === 'rich' || ev.id === 'source') this.setMode(ev.id);
    this.modeMenuOpen.set(false);
  }

  // Last value the parent told us about — anything diverging from this is "dirty".
  private readonly committedValue = signal('');

  readonly canAttach = computed(() => !!this.jobId());

  readonly state = computed<EditorState>(() => resolveEditorState(this.savedAt(), this.sourceValue(), this.committedValue()));
  readonly saveState = computed(() => editorSaveState(this.state()));
  readonly lockReason = computed(() => this.readOnlyReason() || DEFAULT_EDITOR_LOCK_REASON);

  @ViewChild('editorHost') private editorHost?: ElementRef<HTMLElement>;
  @ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;

  private editor: Editor | null = null;
  private destroyed = false;
  private savedTimer: ReturnType<typeof setTimeout> | null = null;
  private autosaveTimer: ReturnType<typeof setTimeout> | null = null;
  private dragCounter = 0;
  private readonly mediaLightbox = inject(MediaLightboxService);
  /**
   * True only after a real user edit has touched the editor. Tiptap's
   * onUpdate fires for both user input and (in some configurations) the
   * initial constructor content load, which used to race against the
   * parent's async detail() fetch: an autosave fired with the stub empty
   * value before the real prompt arrived, clobbering prompt.md on disk.
   * We gate scheduleAutosave on this flag and flip it only inside an
   * onUpdate that observes content actually different from
   * <see cref="committedValue"/>. Programmatic setContent calls in
   * <see cref="valueEffect"/> use `emitUpdate: false` so they never flip it.
   */
  private hasUserEdit = false;
  // Sync the parent-provided value into the editor whenever it changes. We
  // read sourceValue/committedValue via untracked() so user edits (which write
  // sourceValue) don't re-trigger this effect and revert the user's typing.
  private readonly valueEffect = effect(() => {
    const next = this.value() ?? '';
    untracked(() => {
      const prevCommitted = this.committedValue();
      this.committedValue.set(next);
      if (next === this.sourceValue()) return;
      // Skip reset when the server echoes back the value we just autosaved
      // while the user has already typed more content. Without this guard an
      // autosave round-trip (save → re-fetch → valueEffect) would overwrite
      // in-progress typing with the slightly-stale saved value.
      if (next === prevCommitted && this.sourceValue() !== prevCommitted) return;
      this.sourceValue.set(next);
      if (this.editor) {
        this.editor.commands.setContent(this.toHtml(next), { emitUpdate: false });
      }
    });
  });

  private readonly readOnlyEffect = effect(() => {
    const locked = this.readOnly();
    untracked(() => {
      this.editor?.setEditable(!locked);
    });
  });

  ngAfterViewInit(): void {
    void this.initEditor();
  }

  private async initEditor(): Promise<void> {
    this.sourceValue.set(this.value() ?? '');
    const [{ Editor }, { default: StarterKit }, imageMod] = await Promise.all([
      import('@tiptap/core'),
      import('@tiptap/starter-kit'),
      import('@tiptap/extension-image')
    ]);

    if (this.destroyed) return;

    const Image = imageMod.default ?? imageMod.Image;

    this.editor = new Editor({
      element: this.editorHost?.nativeElement,
      extensions: [
        StarterKit,
        Image.configure({ inline: false, allowBase64: false })
      ],
      content: this.toHtml(this.sourceValue()),
      editable: !this.readOnly(),
      editorProps: {
        // Attach the shared markdown typography to the contenteditable
        // surface so the live edit view matches the protocol pane.
        attributes: {
          class: 'markdown-body markdown-body--editor'
        },
        handleKeyDown: (_view, event) => {
          if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
            event.preventDefault();
            this.emitSave();
            return true;
          }
          return false;
        },
        // When the editor is read-only (e.g. CLI is running for this
        // task), a click on an inline image opens the shared lightbox
        // instead of acting as a TipTap selection. Single-click in
        // editable mode still selects the image for normal editing -
        // double-click opens the lightbox there.
        handleClick: (_view, _pos, event) => {
          if (!this.readOnly()) return false;
          const target = event.target as HTMLElement | null;
          if (!target || target.tagName !== 'IMG') return false;
          const img = target as HTMLImageElement;
          event.preventDefault();
          this.mediaLightbox.open({
            src: img.currentSrc || img.src,
            alt: img.getAttribute('alt') ?? '',
          });
          return true;
        },
        handleDoubleClick: (_view, _pos, event) => {
          const target = event.target as HTMLElement | null;
          if (!target || target.tagName !== 'IMG') return false;
          const img = target as HTMLImageElement;
          event.preventDefault();
          this.mediaLightbox.open({
            src: img.currentSrc || img.src,
            alt: img.getAttribute('alt') ?? '',
          });
          return true;
        },
        handlePaste: (_view, event) => {
          const file = this.imageFromClipboard(event.clipboardData);
          if (!file) return false;
          event.preventDefault();
          void this.uploadAndInsert(file);
          return true;
        },
        handleDrop: (_view, event, _slice, moved) => {
          if (moved) return false;
          const dt = (event as DragEvent).dataTransfer;
          const file = this.imageFromDataTransfer(dt);
          if (!file) return false;
          event.preventDefault();
          void this.uploadAndInsert(file);
          return true;
        }
      },
      onUpdate: ({ editor }) => {
        const next = htmlToMarkdown(editor.getHTML(), this.markdownOptions());
        this.sourceValue.set(next);
        // Only count this as a user edit (and therefore arm autosave) when
        // the new content actually diverges from what the parent told us.
        // Tiptap fires onUpdate for the constructor's content load on some
        // versions; without this guard a stub empty mount would autosave ''
        // before the real prompt arrives and clobber prompt.md on disk.
        if (next !== this.committedValue()) {
          this.hasUserEdit = true;
          this.scheduleAutosave();
        }
      }
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.valueEffect.destroy();
    this.readOnlyEffect.destroy();
    this.editor?.destroy();
    if (this.savedTimer) clearTimeout(this.savedTimer);
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
  }

  setMode(mode: 'rich' | 'source'): void {
    this.mode.set(mode);
  }

  updateSource(value: string): void {
    this.sourceValue.set(value);
    this.editor?.commands.setContent(this.toHtml(value), { emitUpdate: false });
    // Source-mode edit is unambiguously user-driven (the textarea binds to
    // ngModelChange); flag hasUserEdit so the no-clobber rule lets the
    // resulting autosave through.
    if (value !== this.committedValue()) this.hasUserEdit = true;
    this.scheduleAutosave();
  }

  private scheduleAutosave(): void {
    if (this.readOnly()) return;
    if (!this.autoSave()) return;
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
    this.autosaveTimer = setTimeout(() => {
      this.autosaveTimer = null;
      this.emitSave();
    }, 600);
  }

  handleKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.emitSave();
    }
  }

  // Catch Ctrl+S anywhere on the page while the editor is mounted, so users
  // don't have to focus the editor first. The detail view hosts a single
  // prompt editor at a time, so a window-level listener is unambiguous.
  @HostListener('window:keydown', ['$event'])
  onWindowKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      this.emitSave();
    }
  }

  emitSave(): void {
    if (this.readOnly()) return;
    const value = this.sourceValue();
    // No-clobber rule: don't persist the editor value unless the user
    // actually touched it AND it diverges from what the parent committed.
    // Without this guard, the empty initial-mount race overwrites
    // prompt.md before the parent's async detail() fetch arrives. See
    // shouldEmitEditorSave for the full rationale.
    if (!shouldEmitEditorSave({
      current: value,
      committed: this.committedValue(),
      hasUserEdit: this.hasUserEdit
    })) return;
    this.committedValue.set(value);
    // After a real save, clear the user-edit flag so a fresh remount-with-
    // stub-value race cannot reuse the latched true to push another empty.
    // The next user keystroke will set it again.
    this.hasUserEdit = false;
    this.save.emit(value);
    if (this.savedTimer) clearTimeout(this.savedTimer);
    this.savedAt.set(Date.now());
    this.savedTimer = setTimeout(() => {
      this.savedAt.set(0);
      this.savedTimer = null;
    }, 1400);
  }

  triggerFilePicker(): void {
    if (this.readOnly() || this.uploading()) return;
    this.fileInput?.nativeElement.click();
  }

  onFileInputChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) void this.uploadAndInsert(file);
    input.value = '';
  }

  handleSourcePaste(event: ClipboardEvent): void {
    const file = this.imageFromClipboard(event.clipboardData);
    if (!file) return;
    event.preventDefault();
    void this.uploadAndInsert(file);
  }

  onDragOver(event: DragEvent): void {
    if (!this.canAttach() || this.readOnly()) return;
    if (!event.dataTransfer) return;
    if (!Array.from(event.dataTransfer.types).includes('Files')) return;
    event.preventDefault();
    this.dragCounter = Math.max(this.dragCounter, 1);
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent): void {
    if (!this.isDragging()) return;
    // Only reset when the cursor actually leaves the host, not just moves
    // between child elements.
    if (event.target !== event.currentTarget) return;
    this.isDragging.set(false);
    this.dragCounter = 0;
  }

  onDrop(event: DragEvent): void {
    this.isDragging.set(false);
    this.dragCounter = 0;
    if (!this.canAttach() || this.readOnly()) return;
    const file = this.imageFromDataTransfer(event.dataTransfer);
    if (!file) return;
    event.preventDefault();
    // Source-mode drop: insert an attachments/<file> reference at the end.
    if (this.mode() === 'source') {
      void this.uploadAndAppendSource(file);
    } else {
      void this.uploadAndInsert(file);
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

  private imageFromDataTransfer(data: DataTransfer | null): File | null {
    if (!data) return null;
    for (const file of Array.from(data.files ?? [])) {
      if (file.type.startsWith('image/')) return file;
    }
    return null;
  }

  private async uploadAndInsert(file: File): Promise<void> {
    const upload = await this.uploadAttachment(file);
    if (!upload) return;
    if (this.editor) {
      this.editor
        .chain()
        .focus()
        .setImage({ src: upload.absoluteUrl, alt: upload.alt })
        .run();
    } else {
      // Editor not yet ready — append directly to the source instead.
      await this.uploadAndAppendSource(file, upload);
    }
  }

  private async uploadAndAppendSource(file: File, upload?: UploadResult | null): Promise<void> {
    const result = upload ?? (await this.uploadAttachment(file));
    if (!result) return;
    const ref = `\n\n![${result.alt}](${result.relativePath})\n`;
    this.updateSource((this.sourceValue() ?? '') + ref);
  }

  private async uploadAttachment(file: File): Promise<UploadResult | null> {
    const jobId = this.jobId();
    if (!jobId) {
      this.uploadError.set('Cannot attach images on this editor instance.');
      return null;
    }
    if (this.readOnly()) return null;
    if (file.size > 10 * 1024 * 1024) {
      this.uploadError.set('Image too large (max 10 MB).');
      return null;
    }

    this.uploadError.set(null);
    this.uploading.set(true);
    try {
      const watchPath = this.watchPath();
      const url = `/api/tasks/${encodeURIComponent(jobId)}/attachments`
        + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
      const form = new FormData();
      form.append('file', file, file.name || 'pasted-image.png');
      const res = await sessionFetch(url, { method: 'POST', body: form });
      if (!res.ok) {
        const body = await res.text().catch(() => '');
        this.uploadError.set(`Upload failed (${res.status}): ${body || res.statusText}`);
        return null;
      }
      const payload = (await res.json()) as { fileName: string; relativePath: string; url: string };
      return {
        fileName: payload.fileName,
        relativePath: payload.relativePath,
        absoluteUrl: this.absoluteAttachmentUrl(payload.relativePath),
        alt: this.deriveAlt(file)
      };
    } catch (err) {
      this.uploadError.set(`Upload failed: ${(err as Error).message ?? err}`);
      return null;
    } finally {
      this.uploading.set(false);
    }
  }

  private deriveAlt(file: File): string {
    const stem = (file.name ?? '').replace(/\.[^.]+$/, '').trim();
    return stem || 'screenshot';
  }

  private absoluteAttachmentUrl(relativePath: string): string {
    const jobId = this.jobId();
    if (!jobId) return relativePath;
    const watchPath = this.watchPath();
    const fileName = relativePath.startsWith(ATTACHMENTS_PREFIX)
      ? relativePath.slice(ATTACHMENTS_PREFIX.length)
      : relativePath;
    const qs = watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '';
    return `/api/tasks/${encodeURIComponent(jobId)}/attachments/${encodeURIComponent(fileName)}${qs}`;
  }

  private markdownOptions(): MarkdownImageOptions {
    return {
      resolveImageSrc: (src) => {
        if (!src.startsWith(ATTACHMENTS_PREFIX)) return src;
        return this.absoluteAttachmentUrl(src);
      },
      serializeImageSrc: (src) => {
        // Pull `attachments/<file>` back out of an absolute API URL so prompt.md
        // on disk keeps the same relative reference the agent can resolve.
        const match = /\/attachments\/([^/?#]+)/.exec(src);
        if (match) return `${ATTACHMENTS_PREFIX}${decodeURIComponent(match[1])}`;
        return src;
      }
    };
  }

  private toHtml(markdown: string): string {
    return markdownToHtml(markdown, this.markdownOptions());
  }
}

interface UploadResult {
  fileName: string;
  relativePath: string;
  absoluteUrl: string;
  alt: string;
}
