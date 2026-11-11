/** Shell feature public API. Cycle 9h / ADR-0034. */
export { StatusBarComponent } from './components/status-bar/status-bar';
// AutoReviewIndicatorComponent and StatusBarUsageHostComponent are legacy-layout chrome; see ./lazy.
export { AccountControlComponent } from './components/account-control/account-control';
export { WorkspaceBannerComponent } from './components/workspace-banner/workspace-banner';
export { WorkspaceOverlaysHostComponent } from './components/workspace-overlays-host/workspace-overlays-host.component';
export { UiPreferencesService } from './state/ui-preferences.service';
export { WorkspaceOverlaysService } from './state/workspace-overlays.service';
export { FirstRunLaunchService } from './state/first-run-launch.service';
export type { WorkspaceSettingsSection } from './state/workspace-overlays.service';
export { WorkspaceManagerService } from './state/workspace-manager.service';
export { ProjectDragDropService } from './state/project-drag-drop.service';
