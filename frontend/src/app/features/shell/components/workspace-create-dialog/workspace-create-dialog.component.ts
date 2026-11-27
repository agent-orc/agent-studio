import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, AfterViewInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { WorkspaceManagerService } from '../../state/workspace-manager.service';

/**
 * Modal form for "+ Add workspace". Mounted once at the shell behind a
 * <code>WorkspaceManagerService.createOpen</code> signal so any trigger
 * (titlebar "Workspace" button, Explorer "+", future deep link) opens
 * the same surface. Hands off to
 * <code>TaskService.createRegistryWorkspace</code> on submit, then
 * refreshes the workspace registry so the new workspace lights up in
 * the sidebar tree before the dialog closes.
 *
 * Inline validation mirrors the backend rule (non-empty, length capped,
 * unique slug); the UI also pre-checks against the already-loaded
 * registry workspaces so a likely-doomed submit fails fast without a
 * round trip.
 */
@Component({
  selector: 'app-workspace-create-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DialogComponent],
  templateUrl: './workspace-create-dialog.component.html',
  styleUrl: './workspace-create-dialog.component.scss',
})
export class WorkspaceCreateDialogComponent implements AfterViewInit {
  private readonly jobService = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly manager = inject(WorkspaceManagerService);

  readonly draftName = signal('');
  readonly submitting = signal(false);
  readonly errorMsg = signal<string | null>(null);

  @ViewChild('nameInput') private nameInputRef?: ElementRef<HTMLInputElement>;

  readonly clientErrorMsg = computed(() => {
    const raw = this.draftName().trim();
    if (raw.length === 0) return null;
    if (raw.length > 64) return 'Name must be 64 characters or fewer.';
    const existing = this.manager.knownNames();
    if (existing.some(n => n.toLowerCase() === raw.toLowerCase())) {
      return `A workspace named "${raw}" already exists.`;
    }
    return null;
  });

  readonly canSubmit = computed(() => {
    const raw = this.draftName().trim();
    if (raw.length === 0) return false;
    if (this.submitting()) return false;
    if (this.clientErrorMsg() !== null) return false;
    return true;
  });

  ngAfterViewInit(): void {
    queueMicrotask(() => this.nameInputRef?.nativeElement.focus());
  }

  onCancel(): void {
    if (this.submitting()) return;
    this.manager.closeCreate();
  }

  onSubmit(): void {
    if (!this.canSubmit()) return;
    const name = this.draftName().trim();
    this.submitting.set(true);
    this.errorMsg.set(null);
    this.jobService.createRegistryWorkspace(name).subscribe({
      next: () => {
        this.submitting.set(false);
        this.notifications.success(`Workspace "${name}" created.`);
        this.manager.refreshAndClose();
      },
      error: (err: unknown) => {
        this.submitting.set(false);
        const msg = formatError(err);
        this.errorMsg.set(msg);
        this.notifications.error(`Could not create workspace "${name}": ${msg}`);
      },
    });
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onSubmit();
    }
  }
}

function formatError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { error?: string } | null;
    if (body?.error) return body.error;
    if (err.status === 0) return 'Backend unreachable. Try again in a moment.';
    return `Create failed (HTTP ${err.status}).`;
  }
  return 'Create failed.';
}
