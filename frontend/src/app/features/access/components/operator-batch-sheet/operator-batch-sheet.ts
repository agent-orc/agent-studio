import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { routeSegmentOf, withRouteSegment } from '../../../../services/url-hash.util';
import { formatAccessTime } from '../../models/access.model';
import { instructionSourceLabel, isUntrustedSource } from '../../models/operator-approval.model';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { OperatorBatchesService } from '../../services/operator-batches.service';
import type { OperatorBatch, OperatorBatchItem } from '../../models/operator-batch.model';
import { AccessOperatorApprovalsComponent } from '../access-operator-approvals/access-operator-approvals.component';

@Component({
  selector: 'app-operator-batch-sheet',
  imports: [DialogComponent, AccessOperatorApprovalsComponent],
  templateUrl: './operator-batch-sheet.html',
  styleUrl: './operator-batch-sheet.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperatorBatchSheetComponent {
  readonly service = inject(OperatorBatchesService);
  readonly focused = signal<string | null>(null);
  readonly single = signal(false);
  readonly selected = signal<ReadonlySet<number>>(new Set());
  readonly code = signal('');
  readonly batch = computed(() => this.service.batches().find(batch => batch.id === this.focused()));
  readonly pending = computed(() => this.service.batches().filter(batch => batch.state === 'pending'));
  readonly time = formatAccessTime;
  readonly sourceText = instructionSourceLabel;
  readonly untrusted = isUntrustedSource;
  readonly executed = computed(() => this.batch()?.items.filter(item => item.state === 'executed').length ?? 0);
  readonly classLabels: Readonly<Partial<Record<string, string>>> = {
    'cards.accept-override': 'Accept with review reservations',
    'cards.archive-override': 'Archive without protected delivery',
  };
  readonly close = () => {
    this.focused.set(null);
    this.single.set(false);
    this.code.set('');
    this.selected.set(new Set());
    const route = routeSegmentOf(window.location.hash);
    if (!route) return;
    const [path, query] = route.split('?');
    const parameters = new URLSearchParams(query ?? '');
    parameters.delete('batch');
    parameters.delete('approval');
    const hash = withRouteSegment(window.location.hash, `${path}${parameters.size ? `?${parameters}` : ''}`);
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search + hash);
  };

  constructor() {
    const follow = async () => {
      await this.service.load();
      const query = new URLSearchParams(routeSegmentOf(window.location.hash)?.split('?')[1] ?? '');
      this.single.set(query.has('approval'));
      const batch = this.service.batches().find(batch => batch.id === query.get('batch'));
      if (batch) this.open(batch);
    };
    void follow();
    window.addEventListener('hashchange', follow);
    const poll = setInterval(() => { if (this.focused()) void this.service.load(); }, 5000);
    inject(DestroyRef).onDestroy(() => { window.removeEventListener('hashchange', follow); clearInterval(poll); });
  }

  open(batch: OperatorBatch): void {
    this.focused.set(batch.id);
    this.selected.set(new Set(batch.items.filter(item => !item.refusal).map(item => item.index)));
    this.code.set('');
  }
  toggle(index: number, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(index); else next.delete(index);
    this.selected.set(next);
  }
  link(item: OperatorBatchItem): string {
    return window.location.pathname + withRouteSegment('', `/tasks/${encodeURIComponent(item.request.card)}`);
  }
  async decide(approve: boolean): Promise<void> {
    const batch = this.batch();
    if (!batch || this.service.busy()) return;
    await this.service.decide(batch.id, [...this.selected()], approve, this.code());
    this.code.set('');
  }
}
