import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import {
  UsageCockpitHeaderComponent,
  type CockpitNavItem,
  type UsageDetailRequest,
} from '../../../app/features/usage-cockpit';
import { FIXTURE_NOW, SCENARIOS } from './usage-header.fixtures';

/**
 * HUC-S4 fixture harness. Mounts the real `app-usage-cockpit-header` with
 * pinned synthetic data and a stand-in navigation row that uses the same
 * `data-nav-inline-from` contract and More destinations as the studio shell.
 *
 * Query: `theme=light|dark`, `scenario=default|three|long|large|loading`,
 * `container=<px>` to constrain the header inside a narrower box, and
 * `selected=<cliId>` / `default=<cliId>` for the primary order.
 */
@Component({
  selector: 'mockup-usage-header-harness',
  standalone: true,
  imports: [UsageCockpitHeaderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-header-harness.component.html',
  styleUrl: './usage-header-harness.component.scss',
})
export class UsageHeaderHarnessComponent {
  private readonly params = new URLSearchParams(location.search);
  readonly theme = this.params.get('theme') === 'light' ? 'light' : 'dark';
  readonly snapshot = SCENARIOS[this.params.get('scenario') ?? 'default'] ?? null;
  readonly container = Number(this.params.get('container')) || null;
  readonly selected = signal(this.params.get('selected'));
  readonly defaultCli = this.params.get('default');
  readonly now = FIXTURE_NOW;

  readonly navItems: readonly CockpitNavItem[] = [
    { id: 'project', label: 'Switch project', inlineFrom: 'tablet' },
    { id: 'search', label: 'Search', inlineFrom: 'tablet' },
    { id: 'chat', label: 'Project chat', inlineFrom: 'tablet' },
    { id: 'theme', label: 'Switch theme', inlineFrom: 'desktop' },
    { id: 'usage', label: 'Usage details', inlineFrom: null },
    { id: 'feed', label: 'Orchestrator feed', inlineFrom: null },
    { id: 'settings', label: 'Orchestrator settings', inlineFrom: null },
  ];

  /** Last destination or usage section requested, for the spec to read. */
  readonly lastAction = signal('');
  readonly openDetail = signal<UsageDetailRequest | null>(null);

  constructor() {
    document.documentElement.setAttribute('data-studio-theme', this.theme);
  }

  onNav(id: string): void {
    this.lastAction.set(`nav:${id}`);
  }

  onUsage(request: UsageDetailRequest): void {
    if (request.section === 'cli' && request.cliId) this.selected.set(request.cliId);
    this.lastAction.set(`usage:${request.section}${request.cliId ? ':' + request.cliId : ''}`);
  }
}
