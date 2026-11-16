import { inject, Injectable } from '@angular/core';
import { ConfirmDialogService } from './confirm-dialog.service';

export interface ProjectLifecycleTarget {
  projectId: string;
  displayName: string;
  shortCode: string | null;
}

/** The same consequence copy and delete gate for Settings and Explorer. */
@Injectable({ providedIn: 'root' })
export class ProjectLifecycleDialogService {
  private readonly dialogs = inject(ConfirmDialogService);

  confirmArchive(project: ProjectLifecycleTarget, archived: boolean): Promise<boolean> {
    const name = `${project.displayName || project.projectId} (${project.projectId})`;
    return this.dialogs.confirm({
      title: `${archived ? 'Restore' : 'Archive'} ${name}?`,
      message: archived
        ? 'This shows the project in Explorer again and makes its open cards available for pickup. Its cards and server checkout stay on disk. Branches on origin are unchanged.'
        : 'This hides the project and its open cards from Explorer and stops new pickup for this project. Its cards and server checkout stay on disk. Branches on origin are unchanged.',
      confirmLabel: archived ? 'Restore project' : 'Archive project',
      kind: 'primary',
    });
  }

  async confirmDelete(project: ProjectLifecycleTarget): Promise<boolean> {
    const name = project.displayName || project.projectId;
    const first = await this.dialogs.confirm({
      title: `Delete ${name} (${project.projectId})?`,
      message: 'This closes access to open cards and stops pickup. It permanently removes the project and all its cards and files from server storage. A Studio-managed server checkout is removed; an external checkout is kept. Branches on origin are unchanged.',
      confirmLabel: 'Continue',
      kind: 'danger',
    });
    if (!first) return false;
    const values = [name, ...(project.shortCode ? [project.shortCode] : [])];
    return this.dialogs.confirm({
      title: 'Confirm permanent deletion',
      message: `Delete ${name} (${project.projectId}) and its project storage? The managed checkout is removed; branches on origin are kept.`,
      requireTypedValues: values,
      requireTypedPrompt: project.shortCode
        ? `Type the project name "${name}" or its code "${project.shortCode}" to confirm.`
        : `Type the project name "${name}" to confirm.`,
      confirmLabel: 'Delete project',
      kind: 'danger',
    });
  }
}
