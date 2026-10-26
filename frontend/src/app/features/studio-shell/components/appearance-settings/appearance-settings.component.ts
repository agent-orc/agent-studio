import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import {
  SegmentedControlComponent,
  SegmentedOption,
} from '../../../../components/segmented-control/segmented-control.component';
import { ThemeService } from '../../services/theme.service';
import { StudioPanelStateService } from '../../services/studio-panel-state.service';
// WorkspaceOverlaysComponent imports this component and is itself exported by
// the shell barrel. Read the leaf service directly to avoid that barrel cycle.
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';

/**
 * AGT-2035 — Appearance section of the consolidated Workspace-settings view.
 *
 * Holds the two user-level layout preferences that survived the settings
 * clean-up: **Theme** (now global-only, shared with the titlebar quick-toggle
 * via {@link ThemeService}) and **Activity bar** side. The former sibling
 * controls "Project chat rail" and "Card density" were removed (dead features).
 *
 * These are per-user preferences, so this component lives in the "Global"
 * rail group; it is rendered by `WorkspaceOverlaysComponent`.
 */
@Component({
  selector: 'app-appearance-settings',
  standalone: true,
  imports: [SectionHeaderComponent, SegmentedControlComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './appearance-settings.component.html',
  styleUrl: './appearance-settings.component.scss',
})
export class AppearanceSettingsComponent {
  private readonly themeService = inject(ThemeService);
  private readonly panelState = inject(StudioPanelStateService);
  readonly uiPrefs = inject(UiPreferencesService);

  readonly theme = this.themeService.preference;
  readonly activityBarSide = this.panelState.activityBarSide;

  readonly themeOptions: readonly SegmentedOption<'system' | 'dark' | 'light'>[] = [
    { value: 'system', label: 'System', testid: 'settings-theme-system' },
    { value: 'dark', label: 'Dark', testid: 'settings-theme-dark' },
    { value: 'light', label: 'Light', testid: 'settings-theme-light' },
  ];
  readonly activityBarSideOptions: readonly SegmentedOption<'left' | 'right'>[] = [
    { value: 'left', label: 'Left', testid: 'settings-activitybar-left' },
    { value: 'right', label: 'Right', testid: 'settings-activitybar-right' },
  ];
  readonly treeMetricViewOptions: readonly SegmentedOption<'numbers' | 'dots'>[] = [
    { value: 'numbers', label: 'Numbers', testid: 'settings-tree-metrics-numbers' },
    { value: 'dots', label: 'Dots', testid: 'settings-tree-metrics-dots' },
  ];
  readonly projectChatEntryOptions: readonly SegmentedOption<'open' | 'closed'>[] = [
    { value: 'open', label: 'Open', testid: 'settings-project-chat-entry-open' },
    { value: 'closed', label: 'Keep closed', testid: 'settings-project-chat-entry-closed' },
  ];

  setTheme(value: 'system' | 'dark' | 'light'): void {
    this.themeService.set(value);
  }

  setActivityBarSide(side: 'left' | 'right'): void {
    this.panelState.setActivityBarSide(side);
  }

  setTreeMetricView(value: 'numbers' | 'dots'): void {
    this.uiPrefs.setTreeMetricView(value);
  }

  setProjectChatEntry(value: 'open' | 'closed'): void {
    this.uiPrefs.setOpenProjectChatOnEntry(value === 'open');
  }
}
