import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { PipelineAdminRow } from '../pipeline-config.util';
import {
  pipelineEffectiveRouteSummary,
  pipelineQuotaAdmissionExplanation,
  pipelineQuotaAdmissionIsWait,
} from '../pipeline-config.util';

/** Read-only quota-admitted route shown beside, never inside, the configured route editor. */
@Component({
  selector: 'app-pipeline-quota-route',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-quota-route.component.html',
  styleUrl: './pipeline-quota-route.component.scss',
})
export class PipelineQuotaRouteComponent {
  readonly step = input.required<PipelineAdminRow>();
  readonly presentation = input<'summary' | 'detail'>('summary');

  readonly summary = computed(() => pipelineEffectiveRouteSummary(this.step()));
  readonly explanation = computed(() => pipelineQuotaAdmissionExplanation(this.step()));
  readonly isFallback = computed(() => !!this.step().quotaAdmission?.isFallback);
  readonly isWait = computed(() => pipelineQuotaAdmissionIsWait(this.step()));
  readonly showDetail = computed(() => !!this.step().quotaAdmission || this.step().effectiveRouteChanged);
}
