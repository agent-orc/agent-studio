import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';
import { GitHubAppService, type GitHubAppCoverageState } from '../github-app.service';

/** Repository access per coverage state; tones follow docs/quality/design/status-indicator.md. */
const coverageStates: Readonly<Record<GitHubAppCoverageState, { tone: StatusTone; label: string }>> = {
  covered: { tone: 'success', label: 'Connected' },
  'not-installed': { tone: 'warning', label: 'App not installed' },
  'not-adopted': { tone: 'warning', label: 'Needs confirmation' },
  'no-repository-url': { tone: 'neutral', label: 'No repository' },
  'not-github': { tone: 'neutral', label: 'Not on GitHub' },
  unknown: { tone: 'neutral', label: 'Unknown' },
};

/**
 * Settings > GitHub: one connection that gives every host short-lived,
 * single-repository tokens, with a list of what it reaches and who last fetched.
 */
@Component({
  selector: 'app-github-app-panel',
  standalone: true,
  imports: [DatePipe, DisclosureMarkerComponent, StatusIndicatorComponent],
  templateUrl: './github-app-panel.component.html',
  styleUrl: './github-app-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GitHubAppPanelComponent {
  readonly service = inject(GitHubAppService);

  readonly organization = signal('');
  readonly anyAccount = signal(true);
  readonly existingOpen = signal(false);
  readonly appId = signal('');
  readonly clientId = signal('');
  readonly clientSecret = signal('');
  readonly privateKey = signal('');
  readonly confirmingDisconnect = signal(false);

  readonly status = this.service.status;
  readonly uncovered = computed(() =>
    (this.status()?.repositories ?? []).filter(item => item.state === 'not-installed' || item.state === 'not-adopted').length);
  readonly existingValid = computed(() =>
    Number(this.appId()) > 0 && !!this.clientId().trim() && !!this.clientSecret() && !!this.privateKey().trim());

  constructor() {
    if (!this.service.resumePending()) this.service.load();
  }

  coverage(state: GitHubAppCoverageState): { tone: StatusTone; label: string } {
    return coverageStates[state] ?? coverageStates.unknown;
  }

  connectExisting(): void {
    if (!this.existingValid()) return;
    this.service.connectExisting({
      appId: Number(this.appId()),
      clientId: this.clientId().trim(),
      clientSecret: this.clientSecret(),
      privateKeyPem: this.privateKey(),
    });
    // Write-only: nothing stays in the page once it is sent.
    this.clientSecret.set('');
    this.privateKey.set('');
  }

  disconnect(): void {
    this.confirmingDisconnect.set(false);
    this.service.disconnect();
  }
}
