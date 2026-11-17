import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, computed, effect, inject, input, output, untracked } from '@angular/core';
import { taskServerRouteDetail, taskServerRouteStatus } from '../../models/remote-host.model';
import { heldCauseLabel } from '../../../../models/held-capability-cause';
import type { HeldCapability, RemoteHost, RemoteHostCapabilityHealth } from '../../models/remote-host.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-capability-health',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './capability-health.html',
  styleUrl: './capability-health.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CapabilityHealthComponent {
  readonly host = input.required<RemoteHost>();
  /** AGT-3840: the key a pickup hold or a held review opened this host for. */
  readonly focusCapability = input<string | null>(null);
  /** AGT-3840: the operator asked this role to re-probe one key. */
  readonly reprobe = output<string>();
  readonly heldCauseLabel = heldCauseLabel;
  readonly held = computed<readonly HeldCapability[]>(() => this.host().heldCapabilities ?? []);
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private focusedKey: string | null = null;
  /** Brings the linked key into view once its row has rendered. */
  private readonly focusLinked = effect(() => {
    const key = this.focusCapability();
    if (!key || key === this.focusedKey) return;
    this.held();
    untracked(() => afterNextRender(() => this.scrollTo(key), { injector: this.injector }));
  });

  private scrollTo(key: string): void {
    const row = [...this.element.nativeElement.querySelectorAll<HTMLElement>('[data-capability-key]')]
      .find(item => item.dataset['capabilityKey'] === key);
    if (!row) return;
    this.focusedKey = key;
    row.scrollIntoView?.({ block: 'nearest' });
    row.focus({ preventScroll: true });
  }

  heldTone(hold: HeldCapability): StatusTone {
    return hold.state === 'indeterminate' ? 'warning' : 'danger';
  }
  readonly routeStatus = computed(() => taskServerRouteStatus(this.host()));
  readonly routeDetail = computed(() => taskServerRouteDetail(this.host()));

  tone(capability: RemoteHostCapabilityHealth): 'ok' | 'warn' | 'error' | 'idle' {
    if (!capability.isFresh) return 'error';
    if (capability.advertisedStatus !== 'ready') {
      return capability.advertisedStatus === 'unavailable' ? 'error' : 'warn';
    }
    switch (capability.healthState) {
      case 'healthy': return 'ok';
      case 'suspect': return 'warn';
      case 'draining': return 'error';
      case 'half-open': return 'idle';
    }
  }

  /** Capability health on the shared indicator; a half-open recovery probe is work in progress. */
  indicatorTone(capability: RemoteHostCapabilityHealth): StatusTone {
    switch (this.tone(capability)) {
      case 'ok': return 'success';
      case 'warn': return 'warning';
      case 'error': return 'danger';
      default: return 'running';
    }
  }

  displayState(capability: RemoteHostCapabilityHealth): string {
    if (!capability.isFresh) return 'stale';
    if (capability.advertisedStatus !== 'ready') return capability.advertisedStatus;
    return capability.healthState;
  }
}
