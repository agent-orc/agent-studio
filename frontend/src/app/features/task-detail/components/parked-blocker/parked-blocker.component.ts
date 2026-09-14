import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import type { TaskInfo } from '../../../../models/task.model';
import {
  buildParkedBlockerView,
  type ParkedBlockerView,
} from '../../../../models/parked-blocker-presentation';
import { formatDateTimeUtc } from '../../../../services/format.util';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { TooltipDirective } from 'coding-agent-chat/shared';

/**
 * The authoritative statement about why a parked card is not moving (AGT-2816).
 *
 * AGT-2736 was parked for three days on an operator decision and the card said
 * `Result: Success` with `Open Items: None`: the park reason was on disk and no
 * component rendered it, so the escalation summary derived its headline from the
 * run's own status stub and repeated the run's "Success".
 *
 * This panel renders ABOVE the derived escalation headline because it outranks
 * it: what the run parked itself on is a fact, the headline is an inference. It
 * shows the park type, the question (never the slug in its place), the options
 * the run had already weighed, what would clear the park, the latest recall
 * verdict, and the documents the run named.
 */
@Component({
  selector: 'app-parked-blocker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective],
  templateUrl: './parked-blocker.component.html',
  styleUrl: './parked-blocker.component.scss',
})
export class ParkedBlockerComponent {
  readonly info = input.required<TaskInfo>();

  private readonly tabs = inject(StudioTabStateService);

  /** Null when the card is not parked; the template renders nothing then. */
  readonly view = computed<ParkedBlockerView | null>(() => buildParkedBlockerView(this.info()));

  readonly parkedAtLabel = computed(() => {
    const view = this.view();
    return view ? formatDateTimeUtc(view.parkedAt) : '';
  });

  /**
   * How long the current sweep verdict has held, plus why it reached it. The
   * marker records when a verdict was FIRST observed and an unchanged verdict is
   * never re-persisted, so this reads as "unchanged for 3 days" rather than
   * claiming the sweep last ran then.
   */
  readonly evaluationLine = computed(() => {
    const recall = this.view()?.recall;
    if (!recall) return '';
    const when = recall.heldFor
      ? `unchanged for ${recall.heldFor}`
      : 'no sweep has evaluated this blocker yet';
    return recall.detail ? `${when} · ${recall.detail}` : when;
  });

  /** The freetext park reason, verbatim - it is the record, not a headline. */
  readonly reasonLine = computed(() => this.info().parkedBlocker?.reason?.trim() || 'not recorded');

  /** Open a document the parking run named, in the project wiki. */
  openDocument(relPath: string): void {
    const projectName = this.info().projectName;
    if (!projectName) return;
    this.tabs.open({
      kind: 'hub',
      projectName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath },
    });
  }
}
