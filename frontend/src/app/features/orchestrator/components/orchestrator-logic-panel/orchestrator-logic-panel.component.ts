import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import { FormsModule } from '@angular/forms';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import {
  OrchestratorConfigOption,
  OrchestratorConfigService,
} from '../../../../services/orchestrator-config.service';

interface OptionGroup {
  name: string;
  options: OrchestratorConfigOption[];
}

@Component({
  selector: 'app-orchestrator-logic-panel',
  standalone: true,
  imports: [FormsModule, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-logic-panel.component.html',
  styleUrl: './orchestrator-logic-panel.component.scss',
})
export class OrchestratorLogicPanelComponent implements OnInit {
  readonly config = inject(OrchestratorConfigService);
  readonly snapshot = this.config.snapshot;
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly pending = signal<Record<string, boolean | number | string>>({});

  readonly groups = computed<OptionGroup[]>(() => {
    const snap = this.snapshot();
    if (!snap) return [];
    const order = ['Orchestrator', 'Supervisor', 'Auto-Intervention'];
    const buckets = new Map<string, OrchestratorConfigOption[]>();
    for (const opt of snap.options) {
      const list = buckets.get(opt.group) ?? [];
      list.push(opt);
      buckets.set(opt.group, list);
    }
    return order
      .filter((name) => buckets.has(name))
      .map((name) => ({ name, options: buckets.get(name)! }));
  });

  readonly hasPending = computed(() => Object.keys(this.pending()).length > 0);
  readonly pendingCount = computed(() => Object.keys(this.pending()).length);

  ngOnInit(): void {
    void this.config.load();
  }

  onChange(opt: OrchestratorConfigOption, value: boolean | number | string): void {
    const next = { ...this.pending() };
    if (this.equals(opt.currentValue, value)) delete next[opt.key];
    else next[opt.key] = value;
    this.pending.set(next);
  }

  discard(): void {
    this.pending.set({});
    this.saveError.set(null);
  }

  async save(): Promise<void> {
    if (this.saving() || !this.hasPending()) return;
    this.saving.set(true);
    this.saveError.set(null);
    try {
      await this.config.update(this.pending());
      this.pending.set({});
    } catch (err: unknown) {
      this.saveError.set(this.describe(err));
    } finally {
      this.saving.set(false);
    }
  }

  asBool(v: unknown): boolean {
    return v === true || v === 'true';
  }
  asInt(v: unknown): number {
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : 0;
  }
  formatDefault(opt: OrchestratorConfigOption): string {
    if (opt.defaultValue === null || opt.defaultValue === undefined) return '-';
    return String(opt.defaultValue);
  }
  activeLabel(opt: OrchestratorConfigOption): string {
    const active = opt.activeValue ?? opt.currentValue;
    return active === null || active === undefined ? '-' : String(active);
  }
  valueFor(opt: OrchestratorConfigOption): boolean | number | string | null {
    const pending = this.pending();
    return Object.prototype.hasOwnProperty.call(pending, opt.key)
      ? pending[opt.key]
      : opt.currentValue;
  }

  private equals(a: unknown, b: unknown): boolean {
    if (typeof a === 'number' || typeof b === 'number') return Number(a) === Number(b);
    return a === b;
  }

  private describe(err: unknown): string {
    if (err && typeof err === 'object') {
      const e = err as { error?: { error?: string }; message?: string };
      if (e.error?.error) return e.error.error;
      if (e.message) return e.message;
    }
    return 'Save failed';
  }
}
