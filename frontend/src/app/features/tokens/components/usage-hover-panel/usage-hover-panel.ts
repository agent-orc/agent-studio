import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  OnDestroy,
  computed,
  effect,
  inject,
  output,
  signal,
} from '@angular/core';
import { ModalStackService } from '../../../../services/modal-stack.service';
import type { CliType } from '../../../../models/task.model';
import { HeaderQuotaComponent } from '../../../quota';
import { CliUsageStore } from '../../services/cli-usage.store';
import { CliUsageModalComponent } from '../cli-usage-modal/cli-usage-modal';
import { RemoteHostsService } from '../../../remote-hosts/services/remote-hosts.service';

/**
 * Status-bar quota trigger. Renders the compact <app-header-quota> strip
 * and, when a CLI card is clicked, opens that CLI's own usage-detail
 * modal — one modal per CLI, no shared hover tooltip and no grouped
 * multi-CLI view. The modal's "Manage usage caps" button bubbles
 * `openCliAdmin` up to the shell to reach the full CLI-Management panel
 * (the "Settings-Dach"), where caps are edited.
 *
 * Data comes from the shared `CliUsageStore`: this component starts the
 * lightweight quota poll (`ensureQuotaStarted`) on init and ref-counts
 * the heavy detail aggregates (`startDetail` / `stopDetail`) only while a
 * modal is open, so the always-mounted strip stays cheap.
 */
@Component({
  selector: 'app-usage-hover-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HeaderQuotaComponent, CliUsageModalComponent],
  templateUrl: './usage-hover-panel.html',
  styleUrl: './usage-hover-panel.scss'
})
export class UsageHoverPanelComponent implements OnInit, OnDestroy {
  private readonly store = inject(CliUsageStore);
  private readonly remoteHosts = inject(RemoteHostsService);
  readonly chatUsage = this.remoteHosts.interactiveUsage;
  readonly chatUsageOpen = signal(false);
  private chatUsagePoll: ReturnType<typeof setInterval> | null = null;

  readonly quotaRows = this.store.quotaRows;
  readonly tokens = this.store.tokens;
  readonly adhoc = this.store.adhoc;
  readonly refreshing = this.store.refreshing;

  /** Which CLI's detail modal is open, or null when none is. */
  readonly selectedCli = signal<CliType | null>(null);

  readonly selectedRow = computed(() => {
    const cli = this.selectedCli();
    if (!cli) return null;
    return this.quotaRows().find(r => r.cliType === cli) ?? null;
  });

  readonly openCliAdmin = output<void>();

  private detailStarted = false;

  ngOnInit(): void {
    this.store.ensureQuotaStarted();
  }

  ngOnDestroy(): void {
    if (this.chatUsagePoll) clearInterval(this.chatUsagePoll);
  }

  toggleChatUsage(): void {
    this.chatUsageOpen.update(open => !open);
    if (this.chatUsageOpen()) {
      this.remoteHosts.refreshInteractiveUsage();
      this.chatUsagePoll = setInterval(() => this.remoteHosts.refreshInteractiveUsage(), 5_000);
    } else if (this.chatUsagePoll) {
      clearInterval(this.chatUsagePoll);
      this.chatUsagePoll = null;
    }
  }

  /** Card click from the strip: open that CLI's own detail modal. */
  select(cliType: CliType): void {
    if (this.chatUsageOpen()) this.toggleChatUsage();
    if (!this.detailStarted) {
      this.store.startDetail();
      this.detailStarted = true;
    }
    this.selectedCli.set(cliType);
  }

  close(): void {
    if (this.selectedCli() === null) return;
    this.selectedCli.set(null);
    if (this.detailStarted) {
      this.store.stopDetail();
      this.detailStarted = false;
    }
  }

  refreshOne(cliType: CliType): void {
    this.store.refreshOne(cliType);
  }

  /** Footer "Manage usage caps": close the modal and hand off to the
   *  full CLI-Management panel where caps are edited. */
  manageCaps(): void {
    this.close();
    this.openCliAdmin.emit();
  }

  // Escape / backdrop route through ModalStack so a confirm/error dialog
  // stacked above wins first. The modal registers itself only while open.
  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private modalStackDispose: (() => void) | null = null;
  private readonly modalStackEffect = effect(() => {
    const isOpen = this.selectedCli() !== null;
    if (isOpen && !this.modalStackDispose) {
      this.modalStackDispose = this.modalStack.push('usage-cli-modal', () => this.close());
    } else if (!isOpen && this.modalStackDispose) {
      this.modalStackDispose();
      this.modalStackDispose = null;
    }
  });
  private readonly modalStackTeardown = this.destroyRef.onDestroy(() => {
    this.modalStackDispose?.();
    if (this.detailStarted) this.store.stopDetail();
  });
}
