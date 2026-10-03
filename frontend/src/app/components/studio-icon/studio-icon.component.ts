import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Inline-SVG icon set for the Agent Software Studio shell.
 *
 * Ported 1:1 from the reference layout's icons.jsx (see
 * .reference-layout/icons.jsx in the agent-orchestrator zip). Every path
 * is a VS Code-style line icon on a 24×24 grid; the component renders at
 * `[size]` pixels and inherits `currentColor` so it tints with the host's
 * `color`. This is the canonical icon source — UI surfaces (titlebar,
 * activity bar, sidebar, tab strip, status bar, evidence cards) should
 * use this component instead of Unicode glyphs or emoji.
 */
export type StudioIconName =
  | 'folder' | 'file' | 'code' | 'list' | 'filter' | 'search' | 'cli'
  | 'activity' | 'runbook' | 'settings' | 'plus' | 'close' | 'dot'
  | 'check' | 'expand' | 'collapse' | 'play' | 'pause' | 'branch'
  | 'refresh' | 'more' | 'bell' | 'warn' | 'diff' | 'book' | 'eye'
  | 'panelLeft' | 'panelRight' | 'layout' | 'sliders' | 'bot'
  | 'grid' | 'deck' | 'archive' | 'send' | 'sun' | 'moon' | 'pin'
  | 'epic' | 'backlog' | 'link' | 'star' | 'starFilled' | 'drag'
  | 'chevronRight' | 'chevronDown' | 'chevronLeft' | 'clock' | 'help';

@Component({
  selector: 'app-studio-icon',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-icon.component.html',
})
export class StudioIconComponent {
  readonly name = input.required<StudioIconName>();
  readonly size = input(16);
  readonly strokeWidth = input(1.5);
}
