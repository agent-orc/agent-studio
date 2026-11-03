import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { RemoteHost } from '../../models/remote-host.model';

type GitStatus = RemoteHost['gitPushStatus'];

@Component({
  selector: 'app-git-token-capability',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './git-token-capability.html',
  styleUrl: './git-token-capability.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GitTokenCapabilityComponent {
  readonly status = input<GitStatus>(null);
  readonly detail = input<string | null | undefined>(null);
  readonly mode = input.required<'badges' | 'warning'>();

  readonly contents = computed<{ label: string; tone: StatusTone }>(() => {
    if (this.status() === 'ready' || this.status() === 'ready-no-workflow-scope') {
      return { label: 'Fallback repo: ok', tone: 'success' };
    }
    return this.status() === 'read-only'
      ? { label: 'Fallback repo: blocked', tone: 'danger' }
      : { label: 'Fallback repo: unknown', tone: 'neutral' };
  });
  readonly workflow = computed<{ label: string; tone: StatusTone }>(() => this.status() === 'ready'
    ? { label: 'Fallback workflow: ok', tone: 'success' }
    : this.status() === 'ready-no-workflow-scope'
      ? { label: 'Fallback workflow: permission missing', tone: 'warning' }
      : { label: 'Fallback workflow: unknown', tone: 'neutral' });
}
