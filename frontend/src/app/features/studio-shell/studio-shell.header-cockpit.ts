import { DestroyRef, computed, effect, inject, signal, untracked, type OutputEmitterRef, type Signal, type WritableSignal } from '@angular/core';

import type { RegistryWorkspaceListItem } from '../../models/task.model';
import { ShellAccessService } from '../../services/shell-access.service';
import { clearVisibleInterval, setVisibleInterval } from '../../utils/visible-interval';
import { WorkspaceQuotaBoardState } from '../quota';
import {
  StudioNavigationHeaderComponent,
  UsageCockpitService,
  UsageAlarmStateService,
  type UsageDetailFocus,
  type CockpitNavItem,
  type UsageDetailRequest,
} from '../usage-cockpit';

export { StudioNavigationHeaderComponent };

/** Quota admission changes with metered work, not per keystroke; one read a minute keeps the board current. */
const QUOTA_POLL_MS = 60_000;

/**
 * Destinations of the header More menu. The inline controls with the same
 * meaning carry `data-nav-inline-from` in the shell template; `null` rows are
 * menu-only. Notifications has no destination yet and stays inline-only.
 */
export const STUDIO_HEADER_NAV_ITEMS: readonly CockpitNavItem[] = [
  { id: 'project', label: 'Switch project', inlineFrom: 'tablet' },
  { id: 'search', label: 'Search', inlineFrom: 'tablet' },
  { id: 'chat', label: 'Project chat', inlineFrom: 'tablet' },
  { id: 'theme', label: 'Switch theme', inlineFrom: 'desktop' },
  { id: 'environment', label: 'Environment', inlineFrom: 'tablet' },
  { id: 'settings', label: 'Orchestrator configuration', inlineFrom: null },
];

/** The parts of the studio shell the header cockpit drives. */
export interface StudioHeaderHost {
  readonly registryWorkspaces: Signal<readonly RegistryWorkspaceListItem[]>;
  readonly activeWorkspaceName: Signal<string | null>;
  readonly globalSearchOpen: WritableSignal<boolean>;
  readonly environmentOpen: WritableSignal<boolean>;
  readonly pickerOpen: WritableSignal<boolean>;
  readonly chatToggle: OutputEmitterRef<void>;
  readonly openOrchSettings: OutputEmitterRef<void>;
  toggleTheme(): void;
}

/**
 * Studio-shell wiring for navigation and footer usage detail. It polls the
 * cockpit projection for the active workspace and routes the More menu and
 * footer triggers to their destinations.
 */
export class StudioHeaderCockpit {
  readonly shellAccess = inject(ShellAccessService);
  private currentWorkspaceId: string | null | undefined;
  readonly usage = inject(UsageCockpitService);
  readonly alarms = inject(UsageAlarmStateService);
  private readonly quotaBoard = inject(WorkspaceQuotaBoardState);
  /** Workspace whose quota the board shows: the selected one, else the default (all-projects view). */
  private readonly quotaWorkspaceId = computed<string | null>(() => {
    const workspaces = this.host.registryWorkspaces();
    const name = this.host.activeWorkspaceName();
    return (workspaces.find(ws => ws.displayName === name) ?? workspaces.find(ws => ws.isDefault) ?? workspaces[0])?.id ?? null;
  });
  readonly navItems = this.shellAccess.operator()
    ? STUDIO_HEADER_NAV_ITEMS
    : STUDIO_HEADER_NAV_ITEMS.filter(item => item.id !== 'environment' && item.id !== 'settings');
  readonly detailFocus = signal<UsageDetailFocus | null>(null);
  readonly detailNow = signal(Date.now());
  readonly openRequest = signal<UsageDetailRequest | null>(null);
  /** Anchor of the project picker while it is opened from the More menu. */
  readonly pickerAnchor = signal<HTMLElement | null>(null);

  constructor(private readonly host: StudioHeaderHost) {
    const destroyRef = inject(DestroyRef);
    this.usage.connect(destroyRef);
    effect(() => {
      const workspaceId = this.quotaWorkspaceId();
      untracked(() => this.loadQuota(workspaceId));
    });
    if (this.shellAccess.operator()) {
      const quotaPoll = setVisibleInterval(() => this.loadQuota(this.quotaWorkspaceId()), QUOTA_POLL_MS);
      destroyRef.onDestroy(() => clearVisibleInterval(quotaPoll));
    }
    effect(() => {
      const name = host.activeWorkspaceName();
      const workspaceId = host.registryWorkspaces().find(ws => ws.displayName === name)?.id ?? null;
      if (workspaceId === this.currentWorkspaceId) return;
      this.currentWorkspaceId = workspaceId;
      this.usage.setWorkspace(workspaceId);
      this.alarms.reset();
      this.detailFocus.set(null);
      this.openRequest.set(null);
    });
    effect(() => {
      const snapshot = this.usage.snapshot();
      if (snapshot) untracked(() => this.alarms.ingest(snapshot));
    });
    effect(() => {
      if (!host.pickerOpen()) this.pickerAnchor.set(null);
    });
  }

  onNav(id: string, header: StudioNavigationHeaderComponent): void {
    const host = this.host;
    switch (id) {
      case 'project':
        // Open after this click finishes so the shell's document listener
        // that closes the picker does not see it.
        setTimeout(() => {
          this.pickerAnchor.set(header.moreAnchor());
          host.pickerOpen.set(true);
        });
        return;
      case 'search': host.globalSearchOpen.set(true); return;
      case 'chat': host.chatToggle.emit(); return;
      case 'theme': host.toggleTheme(); return;
      case 'environment': host.environmentOpen.set(true); return;
      case 'settings': host.openOrchSettings.emit(); return;
    }
  }

  onUsage(request: UsageDetailRequest): void {
    this.detailNow.set(Date.now());
    this.openRequest.set(request);
    this.detailFocus.set(request.section === 'cli' && request.cliId
      ? { kind: 'cli', cliId: request.cliId }
      : { kind: 'cost' });
  }

  private loadQuota(workspaceId: string | null): void {
    if (!this.shellAccess.operator()) return;
    if (workspaceId) this.quotaBoard.load(workspaceId);
    else this.quotaBoard.reset();
  }

  closeDetail(): void {
    this.detailFocus.set(null);
    this.openRequest.set(null);
  }
}
