import { DestroyRef, computed, effect, inject, signal, untracked, type OutputEmitterRef, type Signal, type WritableSignal } from '@angular/core';

import type { RegistryWorkspaceListItem } from '../../models/task.model';
import {
  UsageCockpitHeaderComponent,
  UsageCockpitService,
  UsageAlarmStateService,
  type UsageDetailFocus,
  type CockpitNavItem,
  type UsageDetailRequest,
} from '../usage-cockpit';

export { UsageCockpitHeaderComponent };

/** Status-bar storage key of the saved default CLI. */
const STORAGE_DEFAULT_CLI = 'defaultCliType';

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
  { id: 'usage', label: 'Usage details', inlineFrom: null },
  { id: 'feed', label: 'Orchestrator feed', inlineFrom: null },
  { id: 'settings', label: 'Orchestrator settings', inlineFrom: null },
];

/** The parts of the studio shell the header cockpit drives. */
export interface StudioHeaderHost {
  readonly registryWorkspaces: Signal<readonly RegistryWorkspaceListItem[]>;
  readonly activeWorkspaceName: Signal<string | null>;
  readonly globalSearchOpen: WritableSignal<boolean>;
  readonly pickerOpen: WritableSignal<boolean>;
  readonly chatToggle: OutputEmitterRef<void>;
  readonly openUsageSheet: OutputEmitterRef<void>;
  readonly openOrchFeed: OutputEmitterRef<void>;
  readonly openOrchSettings: OutputEmitterRef<void>;
  toggleTheme(): void;
}

/**
 * Studio-shell wiring of the header usage cockpit (HUC-S4). Create it in an
 * injection context. It polls the cockpit projection for the active
 * workspace, keeps the primary-CLI order inputs and routes the More menu
 * and usage triggers to the shell's existing destinations.
 */
export class StudioHeaderCockpit {
  private currentWorkspaceId: string | null | undefined;
  readonly usage = inject(UsageCockpitService);
  readonly alarms = inject(UsageAlarmStateService);
  readonly navItems = STUDIO_HEADER_NAV_ITEMS;
  /** The CLI the operator last opened from the strip leads the primary order. */
  readonly selectedCli = signal<string | null>(null);
  readonly detailFocus = signal<UsageDetailFocus | null>(null);
  readonly detailNow = signal(Date.now());
  readonly openRequest = signal<UsageDetailRequest | null>(null);
  /** Saved default CLI (status-bar selector), re-read on each snapshot. */
  readonly defaultCli = computed(() => {
    this.usage.snapshot();
    return localStorage.getItem(STORAGE_DEFAULT_CLI);
  });
  /** Anchor of the project picker while it is opened from the More menu. */
  readonly pickerAnchor = signal<HTMLElement | null>(null);

  constructor(private readonly host: StudioHeaderHost) {
    this.usage.connect(inject(DestroyRef));
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

  onNav(id: string, header: UsageCockpitHeaderComponent): void {
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
      case 'usage': host.openUsageSheet.emit(); return;
      case 'feed': host.openOrchFeed.emit(); return;
      case 'settings': host.openOrchSettings.emit(); return;
    }
  }

  onUsage(request: UsageDetailRequest): void {
    if (request.section === 'cli' && request.cliId) this.selectedCli.set(request.cliId);
    this.detailNow.set(Date.now());
    this.openRequest.set(request);
    this.detailFocus.set(request.section === 'cli' && request.cliId
      ? { kind: 'cli', cliId: request.cliId }
      : { kind: 'cost' });
  }

  closeDetail(): void {
    this.detailFocus.set(null);
    this.openRequest.set(null);
  }
}
