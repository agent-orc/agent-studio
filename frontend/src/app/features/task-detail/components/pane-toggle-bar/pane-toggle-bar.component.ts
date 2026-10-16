import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { AuthService } from '../../../../services/auth.service';
import { PaneName, PanesVisible } from '../../services/layout-panes.service';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Strip of icon-only toggle buttons for the prompt / protocol / git
 * panes plus the "Open in VS Code" launch shortcut. The label sits on
 * the tooltip; the icon strip itself stays under 28 px tall so the
 * panes below get the freed pixels.
 *
 * The Git button optionally carries a small numeric badge with the
 * commit count attributed to the task (`commitCount > 0`). Replaces
 * the redundant inline "COMMITTED N commits" strip that used to sit
 * above the activity log.
 */
@Component({
  selector: 'app-pane-toggle-bar',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pane-toggle-bar.component.html',
  styleUrl: './pane-toggle-bar.component.scss'
})
export class PaneToggleBarComponent {
  private readonly auth = inject(AuthService);
  readonly localActionsAvailable = computed(() => this.auth.status()?.profile === 'local');
  readonly panesVisible = input.required<PanesVisible>();
  /** Commit count attributed to the task; 0 hides the badge. */
  readonly commitCount = input<number>(0);
  /** Optional tooltip override for the Git toggle when commits exist. */
  readonly gitTooltip = input<string | null>(null);

  readonly toggleRequest = output<PaneName>();
  readonly openInVsCode = output<void>();

  gitTooltipText(): string {
    return this.gitTooltip() ?? 'Git diff & file tree';
  }
}
