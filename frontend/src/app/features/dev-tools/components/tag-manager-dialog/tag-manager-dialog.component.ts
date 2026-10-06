import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TaskService } from '../../../../services/task.service';
import { TagRegistryStore } from '../../../../services/tag-registry.store';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TagRegistryEntry } from '../../../../models/task.model';

type Phase = 'loading' | 'list' | 'error';

interface DraftForm {
  id: string;
  label: string;
  color: string;
  description: string;
}

const DEFAULT_COLOR = '#94a3b8';
const ID_PATTERN = /^[a-z0-9-]{1,32}$/;

/**
 * Slice E follow-up to `backlog-lane-task-types-and-tags`. CRUD surface for
 * the workspace tag registry. Backed by `GET/POST/DELETE /api/tags`; edit is
 * implemented as a same-id delete-then-create round-trip because the API
 * exposes no PUT today and the task spec forbids extending the API. The
 * registry is a single-writer JSON file so a brief gap between delete and
 * recreate is acceptable for a power-user dev dialog; jobs that carry the
 * tag keep their string reference (soft delete) and re-pick the new label /
 * colour from the registry on the next render.
 */
@Component({
  selector: 'app-tag-manager-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent, TooltipDirective],
  templateUrl: './tag-manager-dialog.component.html',
  styleUrl: './tag-manager-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TagManagerDialogComponent implements OnInit {
  private readonly tasks = inject(TaskService);
  private readonly store = inject(TagRegistryStore);
  private readonly confirmDialog = inject(ConfirmDialogService);

  readonly closed = output<void>();

  readonly phase = signal<Phase>('loading');
  readonly errorText = signal<string>('');

  /** Editing state: the tag id currently being edited inline, plus its draft. */
  readonly editingId = signal<string | null>(null);
  readonly editDraft = signal<DraftForm>({ id: '', label: '', color: DEFAULT_COLOR, description: '' });
  readonly editError = signal<string>('');
  readonly editBusy = signal<boolean>(false);

  /** Add-tag state: whether the inline add form is open, plus its draft. */
  readonly addOpen = signal<boolean>(false);
  readonly addDraft = signal<DraftForm>({ id: '', label: '', color: DEFAULT_COLOR, description: '' });
  readonly addError = signal<string>('');
  readonly addBusy = signal<boolean>(false);

  readonly tags = computed<TagRegistryEntry[]>(() => {
    return [...this.store.workspaceTags()].sort((a, b) => a.id.localeCompare(b.id));
  });

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.phase.set('loading');
    this.tasks.listTags().subscribe({
      next: (tags) => {
        this.store.set(tags);
        this.phase.set('list');
      },
      error: (err) => {
        this.errorText.set(this.errMessage(err) || 'Failed to load tags');
        this.phase.set('error');
      },
    });
  }

  // ---- Add ----

  openAdd(): void {
    this.addDraft.set({ id: '', label: '', color: DEFAULT_COLOR, description: '' });
    this.addError.set('');
    this.addOpen.set(true);
  }

  cancelAdd(): void {
    this.addOpen.set(false);
    this.addError.set('');
  }

  patchAddDraft<K extends keyof DraftForm>(key: K, value: DraftForm[K]): void {
    this.addDraft.set({ ...this.addDraft(), [key]: value });
  }

  submitAdd(): void {
    const draft = this.addDraft();
    const label = draft.label.trim();
    if (!label) {
      this.addError.set('Label is required.');
      return;
    }
    const idRaw = draft.id.trim();
    const id = idRaw || this.slugify(label);
    if (!ID_PATTERN.test(id)) {
      this.addError.set('Id must match [a-z0-9-]{1,32}.');
      return;
    }
    this.addBusy.set(true);
    this.addError.set('');
    this.tasks
      .createTag({ id, label, color: draft.color, description: draft.description.trim() })
      .subscribe({
        next: (created) => {
          // Update the store in place; the rest of the app (cards, filter)
          // re-renders synchronously via the signal.
          this.store.set([...this.store.workspaceTags(), created]);
          this.addBusy.set(false);
          this.addOpen.set(false);
        },
        error: (err) => {
          this.addBusy.set(false);
          this.addError.set(this.errMessage(err));
        },
      });
  }

  // ---- Edit ----

  startEdit(tag: TagRegistryEntry): void {
    this.editingId.set(tag.id);
    this.editDraft.set({
      id: tag.id,
      label: tag.label,
      color: tag.color || DEFAULT_COLOR,
      description: tag.description ?? '',
    });
    this.editError.set('');
  }

  cancelEdit(): void {
    this.editingId.set(null);
    this.editError.set('');
  }

  patchEditDraft<K extends keyof DraftForm>(key: K, value: DraftForm[K]): void {
    this.editDraft.set({ ...this.editDraft(), [key]: value });
  }

  submitEdit(): void {
    const draft = this.editDraft();
    const id = draft.id;
    const label = draft.label.trim();
    if (!label) {
      this.editError.set('Label is required.');
      return;
    }
    this.editBusy.set(true);
    this.editError.set('');
    // Shim: delete then recreate with the same id. The TagRegistryEntry's id
    // is the only identity that jobs reference, so the on-job `tags` strings
    // keep matching. A brief gap between the two calls is acceptable for
    // this single-writer dev tool.
    this.tasks.deleteTag(id).subscribe({
      next: () => {
        this.tasks
          .createTag({ id, label, color: draft.color, description: draft.description.trim() })
          .subscribe({
            next: (created) => {
              const next = this.store
                .workspaceTags()
                .filter((t) => t.id !== id)
                .concat(created);
              this.store.set(next);
              this.editBusy.set(false);
              this.editingId.set(null);
            },
            error: (err) => {
              // Recreate failed - refresh the store from the server so the
              // UI does not silently keep a stale row for a now-deleted tag.
              this.editBusy.set(false);
              this.editError.set(this.errMessage(err) || 'Recreate failed - tag may have been removed.');
              this.tasks.listTags().subscribe({
                next: (tags) => this.store.set(tags),
                error: () => {
                  /* leave whatever is in the store */
                },
              });
            },
          });
      },
      error: (err) => {
        this.editBusy.set(false);
        this.editError.set(this.errMessage(err));
      },
    });
  }

  // ---- Delete ----

  async confirmDelete(tag: TagRegistryEntry): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: `Delete tag '${tag.label}'?`,
      message:
        `The tag '${tag.id}' will be removed from the registry. Existing tasks ` +
        'that carry it will render a faint ghost chip until you re-tag them. ' +
        'Default seed tags stay deleted too; the registry merge only adds defaults ' +
        'that were never explicitly removed.',
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      kind: 'danger',
    });
    if (!ok) return;
    this.tasks.deleteTag(tag.id).subscribe({
      next: () => {
        this.store.set(this.store.workspaceTags().filter((t) => t.id !== tag.id));
      },
      error: (err) => {
        // Surface as a top-of-list banner; the row stays so the user can
        // retry.
        this.errorText.set(this.errMessage(err) || 'Delete failed');
      },
    });
  }

  // ---- Helpers ----

  isEditing(id: string): boolean {
    return this.editingId() === id;
  }

  trackById(_: number, tag: TagRegistryEntry): string {
    return tag.id;
  }

  onBackdropClick(): void {
    if (this.editBusy() || this.addBusy()) return;
    this.closed.emit();
  }

  /**
   * Mirror of the backend `NormalizeTagId` flow for the id-auto-derive
   * behaviour when the user leaves the id field blank. Server-side is still
   * authoritative; we just give the user a hint of what they will get.
   */
  private slugify(label: string): string {
    return label
      .toLowerCase()
      .replace(/[^a-z0-9-]+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '')
      .slice(0, 32);
  }

  private errMessage(err: unknown): string {
    const e = err as { error?: { error?: string }; message?: string; status?: number };
    return e?.error?.error || e?.message || (e?.status ? `HTTP ${e.status}` : '');
  }
}
