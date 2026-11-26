import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { formatAccessTime } from '../../models/access.model';
import {
  displaySignInCode,
  normalizeSignInCode,
  shortThumbprint,
  deviceBindingLabel,
  type TerminalSignInLookup,
} from '../../models/terminal-sign-in.model';
import { lifetimeLabel } from '../../services/operator-session-limits.service';
import type { GrantStepUp } from '../../services/standing-grants.service';
import { TerminalSignInRouteService } from '../../services/terminal-sign-in-route.service';
import { TerminalSignInService } from '../../services/terminal-sign-in.service';

/**
 * Confirm terminal sign-in (ES-3, decision ES-D2 option S2). The Owner
 * compares the code with the terminal, sees the device, whether its key is
 * known, the requesting address and whether it matches this browser's, the
 * requested action classes (removable, never addable) and the lifetime, and
 * confirms with a fresh second factor. A link carries the code; it still
 * needs the explicit "this code is on my terminal now" tick.
 */
@Component({
  selector: 'app-terminal-sign-in-confirm',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent],
  templateUrl: './terminal-sign-in-confirm.component.html',
  styleUrl: './terminal-sign-in-confirm.component.scss',
})
export class TerminalSignInConfirmComponent implements OnInit {
  /** The code the confirmation link carried, already normalized; null when the Owner types it. */
  readonly linkCode = input<string | null>(null);

  readonly signIn = inject(TerminalSignInService);
  private readonly route = inject(TerminalSignInRouteService);

  readonly time = formatAccessTime;
  readonly lifetimeLabel = lifetimeLabel;
  readonly allowedLifetimes = computed(() => this.lookup()?.agents.find(agent => agent.id === this.agentId())?.lifetimeHours ?? this.lookup()?.lifetimeHours ?? [8]);
  readonly display = displaySignInCode;
  readonly thumb = shortThumbprint;
  readonly binding = deviceBindingLabel;

  /** True when the Owner types the code instead of using the link's. */
  readonly manual = signal(true);
  readonly codeInput = signal('');
  /** The code the shown details belong to. */
  readonly checkedCode = signal<string | null>(null);
  readonly codeMatches = signal(false);
  readonly agentId = signal<string | null>(null);
  readonly removed = signal<ReadonlySet<string>>(new Set());
  readonly lifetime = signal(8);
  readonly factor = signal('');

  readonly lookup = computed(() => this.signIn.lookup());
  readonly known = computed(() => {
    const found = this.lookup();
    const agent = this.agentId();
    return !!found && !!agent && found.knownForAgentIds.includes(agent);
  });
  readonly requestedAgentMissing = computed(() => {
    const found = this.lookup();
    return !!found?.requestedAgent && !found.requestedAgentId;
  });
  /** Unknown key and another network together are what a phishing attempt looks like. */
  readonly suspicious = computed(() => !!this.lookup() && !this.known() && !this.lookup()!.addressMatches);
  readonly keptClasses = computed(() =>
    (this.lookup()?.requestedClasses ?? []).filter(actionClass => !this.removed().has(actionClass.id)).map(actionClass => actionClass.id));
  readonly canConfirm = computed(() => !!this.lookup() && !!this.agentId() && !this.signIn.busy()
    && (this.manual() || this.codeMatches()));
  readonly codeReady = computed(() => normalizeSignInCode(this.codeInput()) !== null);

  ngOnInit(): void {
    this.signIn.reset();
    const code = this.linkCode();
    if (code) {
      this.manual.set(false);
      void this.check(code);
    }
  }

  async find(event?: Event): Promise<void> {
    event?.preventDefault();
    const code = normalizeSignInCode(this.codeInput());
    if (code && !this.signIn.busy()) await this.check(code);
  }

  /** Types the code after a link whose code could not be used. */
  enterCode(): void {
    this.signIn.reset();
    this.manual.set(true);
    this.checkedCode.set(null);
    this.codeMatches.set(false);
  }

  setAgent(value: string): void {
    this.agentId.set(value || null);
    const selected = this.lookup()?.agents.find(agent => agent.id === value);
    this.lifetime.set(selected?.defaultLifetimeHours ?? this.lookup()?.defaultLifetimeHours ?? 8);
  }

  toggleClass(id: string, kept: boolean): void {
    this.removed.update(current => {
      const next = new Set(current);
      if (kept) next.delete(id); else next.add(id);
      return next;
    });
  }

  setLifetime(value: string): void {
    const hours = Number(value);
    if (this.allowedLifetimes().includes(hours)) this.lifetime.set(hours);
  }

  async confirm(): Promise<void> {
    const code = this.checkedCode();
    const agent = this.agentId();
    if (!code || !agent || !this.canConfirm()) return;
    await this.signIn.confirm({ code, operatorAgentId: agent, classes: this.keptClasses(), lifetimeHours: this.lifetime() }, this.stepUp());
    this.factor.set('');
  }

  async deny(): Promise<void> {
    const code = this.checkedCode();
    if (code && !this.signIn.busy()) await this.signIn.deny(code);
  }

  close(): void {
    this.signIn.reset();
    this.route.close();
  }

  openAccess(): void {
    this.signIn.reset();
    this.route.request.set(null);
    window.location.hash = '#/workspace/settings/access';
  }

  private async check(code: string): Promise<void> {
    this.checkedCode.set(code);
    const found = await this.signIn.find(code);
    if (found) this.prefill(found);
  }

  private prefill(found: TerminalSignInLookup): void {
    this.agentId.set(found.requestedAgentId ?? (found.agents.length === 1 ? found.agents[0].id : null));
    this.removed.set(new Set());
    this.lifetime.set(found.defaultLifetimeHours);
  }

  /** An entered authenticator or recovery code, otherwise a passkey. */
  private stepUp(): GrantStepUp {
    const code = this.factor().trim();
    return code ? { code } : { passkey: true };
  }
}
