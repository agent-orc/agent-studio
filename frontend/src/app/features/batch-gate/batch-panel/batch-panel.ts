import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';
import { ModalStackService } from '../../../services/modal-stack.service';

@Component({
  selector: 'app-batch-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-panel.html',
  styleUrl: './batch-panel.scss',
})
export class BatchPanelComponent implements OnDestroy {
  readonly project = input.required<string>();
  readonly closed = input.required<() => void>();
  readonly gate = inject(BatchGateService);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly error = computed(() => this.gate.error(this.project())());
  readonly confirm = signal<string | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly phases = ['formed', 'assembling', 'assembled', 'running', 'green', 'publishing', 'published', 'red', 'paused', 'abandoned'];
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');
  private readonly confirmButton = viewChild<ElementRef<HTMLButtonElement>>('confirmButton');
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly injector = inject(Injector);
  private readonly previousFocus = document.activeElement as HTMLElement | null;

  constructor() {
    inject(ModalStackService).pushUntilDestroyed('batch-panel', () => this.close(), inject(DestroyRef));
    afterNextRender(() => {
      document.body.appendChild(this.host.nativeElement);
      this.closeButton()?.nativeElement.focus();
    }, { injector: this.injector });
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
  ngOnDestroy(): void {
    this.host.nativeElement.remove();
    queueMicrotask(() => this.previousFocus?.focus());
  }

  date(value?: string): string {
    const stamp = value ? Date.parse(value) : NaN;
    if (!Number.isFinite(stamp)) return 'Unknown';
    return new Date(stamp).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
  }
  duration(start?: string, end?: string): string {
    if (!start) return 'Waiting';
    if (!end) return 'Running';
    const elapsed = Date.parse(end) - Date.parse(start);
    if (!Number.isFinite(elapsed)) return 'Unknown';
    const minutes = Math.max(0, Math.round(elapsed / 60_000));
    return `${minutes} min`;
  }
  phaseLabel(phase: string): string { return phase; }
  showConfirm(target: string): void {
    this.confirm.set(target);
    this.actionError.set(null);
    afterNextRender(() => this.confirmButton()?.nativeElement.focus(), { injector: this.injector });
  }
  close(): void { this.closed()(); }
  act(): void {
    const target = this.confirm();
    if (!target || this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    const request = target === 'close' ? this.gate.closeNow(this.project()) : this.gate.eject(this.project(), target);
    request.subscribe({
      next: () => { this.busy.set(false); this.confirm.set(null); void this.gate.refresh(this.project()); },
      error: err => { this.busy.set(false); this.actionError.set(err?.error?.error ?? 'The action could not be completed.'); },
    });
  }
}
