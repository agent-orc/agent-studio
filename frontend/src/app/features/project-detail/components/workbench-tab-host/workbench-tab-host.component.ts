import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import type { WorkbenchDocument, WorkbenchOverviewItem } from '../../../../models/project-docs.model';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { studioTabKey } from '../../../studio-shell/studio-shell.types';
import { WorkbenchOverviewComponent } from '../workbench-overview/workbench-overview.component';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';

/**
 * Lazy boundary for repository document tabs. These views are opened rarely
 * compared with the board, so their viewer and decision UI stay out of the
 * initial application bundle while preserving the existing tab contract.
 */
@Component({
  selector: 'app-workbench-tab-host',
  standalone: true,
  imports: [LoadingSurfaceComponent, WorkbenchOverviewComponent, WorkbenchViewerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-tab-host.component.html',
  styleUrl: './workbench-tab-host.component.scss',
})
export class WorkbenchTabHostComponent {
  readonly mode = input.required<'overview' | 'viewer'>();
  readonly projectName = input<string | null>(null);
  readonly projectId = input<string | null>(null);
  readonly workbenchId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();
  readonly openWiki = output<{ relPath: string; reuse: 'replace-current' | 'new' }>();
  private readonly tabState = inject(StudioTabStateService);

  /**
   * A tab opened by route (deep link or reload) starts without the
   * catalogue key a click-opened tab already carries. Patch it in once the
   * viewer resolves the document, so the orchestrator side sheet's Dossier
   * scope (AGT-2725) is available regardless of how the tab was opened.
   */
  onDocumentResolved(document: WorkbenchDocument): void {
    const projectName = this.projectName();
    const workbenchId = this.workbenchId();
    if (!projectName || !workbenchId) return;
    const active = this.tabState.activeTab();
    const sourceKey = active?.kind === 'workbench'
      && active.projectName === projectName
      && active.workbenchId === workbenchId
      ? studioTabKey(active)
      : studioTabKey({ kind: 'workbench', projectName, workbenchId });
    this.tabState.retarget(sourceKey, {
      kind: 'workbench',
      projectName,
      workbenchId,
      title: document.workbench.title,
      key: document.workbench.key ?? undefined,
    });
  }
}
