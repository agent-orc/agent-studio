import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import type { WorkspaceSettingsSection } from '../../state/workspace-overlays.service';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import { TokenUsageSectionComponent } from '../../../tokens';
import {
  CliAdminPanelComponent,
  CliWorkingMemoryPanelComponent,
  CliSessionsPanelComponent,
  CliPathsPanelComponent,
} from '../../../cli';
import { RemoteHostsPanelComponent } from '../../../remote-hosts';
import { TaskServerPanelComponent } from '../../../task-server';
import { OrchestratorLogicPanelComponent, PromptAdminPanelComponent } from '../../../orchestrator';
// Direct path (not the studio-shell barrel) so we don't pull StudioShellComponent
// and re-form the shell <-> studio-shell import cycle (AGT-2035).
import { AppearanceSettingsComponent } from '../../../studio-shell/components/appearance-settings/appearance-settings.component';
import { UpdatesSettingsComponent } from '../../../update';
import { WorkspaceManagementComponent } from '../workspace-management/workspace-management.component';
import { RetentionAdminComponent } from '../../../retention';
import type { TaskScreenshot } from '../../../../features/screenshots';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import type { WatchPathEntry } from '../../../../models/task.model';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import {
  StudioIconComponent,
  type StudioIconName,
} from '../../../../components/studio-icon/studio-icon.component';

import { TooltipDirective } from 'coding-agent-chat/shared';

/** Rail-grouping bucket for the consolidated settings sections. */
type SettingsRailGroup = 'general' | 'global' | 'workspace';

interface SettingsRailItem {
  key: WorkspaceSettingsSection;
  label: string;
  description: string;
  icon: StudioIconName;
  group: SettingsRailGroup;
}

/**
 * The one consolidated Workspace-settings view (AGT-2035). It replaces the
 * former split between the studio-shell sidebar "Settings" panel and the
 * scattered workspace overlays: one rail + panel with a clean Global-vs-
 * Workspace grouping.
 *
 * Global group (per-user / app-wide): Appearance (Theme + Activity bar),
 * Updates, Workspaces (registry management), Task Server (the durable task
 * server's URL, store, evidence git, client registry and management sweeps -
 * AGT-1924), Execution Hosts, Orchestrator (the platform-global lifecycle flags
 * AGT-1812 moved out of their standalone modal). Workspace group
 * (defaults applied across the workspace's projects): CLI Management (the CLI
 * catalog / models / routes / usage caps / completion-contracts hub), CLI
 * sessions and CLI paths (encapsulated pages split out of that hub - AGT-2101),
 * Working memory, System prompts, Token usage, Visual evidence.
 *
 * Each content section re-uses a stable outer test id
 * (`cli-admin-overlay`, `prompt-admin-overlay`, `workspace-tokens-overlay`,
 * `workspace-screenshots-overlay`, `orchestrator-config-overlay`, plus the new
 * appearance/updates/workspaces/working-memory ids) so deep-links and specs
 * keep resolving.
 */
@Component({
  selector: 'app-workspace-overlays',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    TokenUsageSectionComponent,
    WorkspaceScreenshotsComponent,
    CliAdminPanelComponent,
    CliWorkingMemoryPanelComponent,
    CliSessionsPanelComponent,
    CliPathsPanelComponent,
    RemoteHostsPanelComponent,
    TaskServerPanelComponent,
    PromptAdminPanelComponent,
    OrchestratorLogicPanelComponent,
    AppearanceSettingsComponent,
    UpdatesSettingsComponent,
    WorkspaceManagementComponent,
    RetentionAdminComponent,
    SectionHeaderComponent,
    TreeRowComponent,
    StudioIconComponent,
    TooltipDirective,
    OverlayPortalDirective,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-overlays.component.html',
  styleUrl: './workspace-overlays.component.scss',
})
export class WorkspaceOverlaysComponent {
  readonly overlays = inject(WorkspaceOverlaysService);
  /** Render inside the Studio editor tab instead of as a modal dialog. */
  readonly inline = input(false);
  readonly watchPaths = input<readonly WatchPathEntry[]>([]);
  readonly openTask = output<TaskScreenshot>();
  /** Project name whose Settings rail the shell should open (bubbled up from
   *  the Token-usage section's per-project usage rows). */
  readonly openProjectSettings = output<string>();
  /** Task the shell should open in its detail panel (bubbled up from the
   *  usage-caps CLI-sessions list). */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalDisposer: (() => void) | null = null;
  readonly collapsedGroups = signal<ReadonlySet<SettingsRailGroup>>(new Set());

  /** Rail order: overview first, then the Global group, then the Workspace group. */
  readonly railItems: readonly SettingsRailItem[] = [
    { key: 'overview', label: 'Overview', description: 'Everything in one place, split into Global and Workspace.', icon: 'grid', group: 'general' },
    { key: 'appearance', label: 'Appearance', description: 'Theme and activity-bar side. Applies to you everywhere.', icon: 'sun', group: 'global' },
    { key: 'updates', label: 'Updates', description: 'Keep this instance in sync with stable.', icon: 'refresh', group: 'global' },
    { key: 'workspaces', label: 'Workspaces', description: 'Manage every workspace and its projects.', icon: 'folder', group: 'global' },
    { key: 'task-server', label: 'Task Server', description: 'Connected URL, workspace store, evidence git, client registry, and management sweeps.', icon: 'file', group: 'global' },
    { key: 'remote-hosts', label: 'Execution Hosts', description: 'Local and remote CLI execution: heartbeat, vitals, quota, and lifecycle.', icon: 'activity', group: 'global' },
    { key: 'orchestrator', label: 'Orchestrator', description: 'Platform-global supervisor, meta-cycle, and auto-intervention lifecycle flags.', icon: 'bot', group: 'global' },
    { key: 'caps', label: 'CLI Management', description: 'What CLIs and models are available, their fallback routes, usage caps and completion contracts.', icon: 'cli', group: 'workspace' },
    { key: 'cli-sessions', label: 'CLI sessions', description: 'Per-CLI per-project native session inventory.', icon: 'list', group: 'workspace' },
    { key: 'cli-paths', label: 'CLI paths', description: 'Where each CLI lives on disk: executable path and known project roots.', icon: 'link', group: 'workspace' },
    { key: 'working-memory', label: 'Working memory', description: 'Per-CLI memory and session state. Auth stays protected.', icon: 'book', group: 'workspace' },
    { key: 'prompts', label: 'System prompts', description: 'Application-wide runtime prompt defaults and overrides.', icon: 'code', group: 'workspace' },
    { key: 'retention', label: 'Retention', description: 'Artifact rules, previews, archive runs, and full backups.', icon: 'archive', group: 'workspace' },
    { key: 'tokens', label: 'Token usage', description: 'The single usage area: token spend across every project.', icon: 'activity', group: 'workspace' },
    { key: 'screenshots', label: 'Visual evidence', description: 'Screenshots captured by tasks across all projects.', icon: 'eye', group: 'workspace' },
  ];

  readonly railGroups: readonly SettingsRailGroup[] = ['general', 'global', 'workspace'];

  /** Human labels for the rail group headers. */
  readonly groupLabels: Record<SettingsRailGroup, string> = {
    general: 'General',
    global: 'Global',
    workspace: 'Workspace',
  };

  /** The sections surfaced as cards on the overview landing. */
  get contentItems(): readonly SettingsRailItem[] {
    return this.railItems.filter(i => i.key !== 'overview');
  }

  railItemsFor(group: SettingsRailGroup): readonly SettingsRailItem[] {
    return this.railItems.filter(item => item.group === group);
  }

  isGroupCollapsed(group: SettingsRailGroup): boolean {
    return this.collapsedGroups().has(group);
  }

  setGroupCollapsed(group: SettingsRailGroup, collapsed: boolean): void {
    const next = new Set(this.collapsedGroups());
    if (collapsed) next.add(group);
    else next.delete(group);
    this.collapsedGroups.set(next);
  }

  constructor() {
    // One modal-stack registration tracks the whole view so Escape and
    // backdrop ordering behave like the other studio modals.
    effect(() => {
      const open = this.overlays.settingsOpen();
      if (this.inline()) {
        this.modalDisposer?.();
        this.modalDisposer = null;
        return;
      }
      if (open && !this.modalDisposer) {
        this.modalDisposer = this.modalStack.push('workspace-settings', () => this.overlays.close());
      } else if (!open && this.modalDisposer) {
        this.modalDisposer();
        this.modalDisposer = null;
      }
    });
    this.destroyRef.onDestroy(() => {
      this.modalDisposer?.();
      this.modalDisposer = null;
    });
  }

  /** Outer test id of the active panel, preserved per section so legacy
   *  deep-link specs keep resolving against the same hook. */
  panelTestid(): string {
    switch (this.overlays.section()) {
      case 'caps': return 'cli-admin-overlay';
      case 'cli-sessions': return 'cli-sessions-overlay';
      case 'cli-paths': return 'cli-paths-overlay';
      case 'prompts': return 'prompt-admin-overlay';
      case 'retention': return 'workspace-retention-overlay';
      case 'tokens': return 'workspace-tokens-overlay';
      case 'screenshots': return 'workspace-screenshots-overlay';
      case 'appearance': return 'workspace-appearance-overlay';
      case 'updates': return 'workspace-updates-overlay';
      case 'workspaces': return 'workspace-management-overlay';
      case 'task-server': return 'workspace-task-server-overlay';
      case 'remote-hosts': return 'workspace-remote-hosts-overlay';
      case 'orchestrator': return 'orchestrator-config-overlay';
      case 'working-memory': return 'workspace-working-memory-overlay';
      case 'overview': return 'workspace-settings-overview-panel';
    }
  }

  onBackdrop(event: Event): void {
    if (event.target === event.currentTarget) this.overlays.close();
  }
}
