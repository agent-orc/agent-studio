import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ResultViewComponent, type ProtocolVerdict } from '../../../app/features/task-detail';
import type { TaskDetail } from '../../../app/models/task.model';

/**
 * Isolated render of the REAL {@link ResultViewComponent} across the case
 * families that pick distinct overview layouts, plus the compact quality-head
 * stats (files changed, tests passed). Backend-free: each card is built
 * from a canned `status.md` + task metadata, exactly as the live protocol pane
 * builds it, so the Playwright spec can screenshot both themes without a running
 * dev backend.
 */
function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    status: 'succeeded',
    signals: [],
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    tooltip: 'from status.md Result line: Last run completed successfully.',
    duration: '4 min',
    superseded: null,
    ...overrides,
  } as ProtocolVerdict;
}

function detail(statusMarkdown: string, overrides: Record<string, unknown> = {}): TaskDetail {
  return {
    info: {
      id: 'mockup-job',
      watchPath: '/ws',
      title: 'Result view mockup',
      taskType: 'chore',
      mode: 'coding',
      tags: ['code-review:grade-a'],
      tokenSummary: { totalTokens: 48_000 },
      commits: [{}, {}],
      codeActivityDetected: true,
      ...overrides,
    },
    statusMarkdown,
  } as unknown as TaskDetail;
}

interface GalleryCard {
  heading: string;
  detail: TaskDetail;
  verdict: ProtocolVerdict;
}

const CARDS: GalleryCard[] = [
  {
    heading: 'bugfix - sequence layout, files + tests stats',
    detail: detail(
      `# Status\n\n- Result: Success\n- Duration: 4 min\n- Files: 3\n- Tests: 24 passed\n\n## Overview\n- Problem: The verdict banner was unreadable on the light theme.\n- Solution: Re-toned it to a neutral surface with a semantic accent stripe.\n`,
      { taskType: 'bug' },
    ),
    verdict: verdict(),
  },
  {
    heading: 'ui-cleanup - before/after two-column layout',
    detail: detail(
      `# Status\n\n- Result: Success\n- Case: ui-cleanup\n- Duration: 6 min\n- Files: 2\n- Tests: 12/12 passed\n\n## Overview\n- Problem: Cards crowded the lane rail with uneven spacing.\n- Solution: Rebuilt the rhythm on the spacing token scale.\n`,
    ),
    verdict: verdict({ duration: '6 min' }),
  },
  {
    heading: 'blocked - warn callout layout',
    detail: detail(
      `# Status\n\n- Result: Blocked\n- Duration: 9 min\n- Files: 1\n- Tests: 3/8 passed\n\n## Overview\n- Problem: Migrate the runner to the new lease API.\n- Solution: Stopped at the lease-renewal race; needs a design decision.\n`,
      { taskType: 'feature' },
    ),
    verdict: verdict({ kind: 'problem', status: 'failed', label: 'Blocked', emoji: '🔴', duration: '9 min' }),
  },
  {
    heading: 'feature - standard stacked layout',
    detail: detail(
      `# Status\n\n- Result: Success\n- Case: feature\n- Duration: 11 min\n- Files: 8\n- Tests: 41 passed\n\n## Overview\n- Problem: Operators had no per-project usage caps.\n- Solution: Added the caps settings surface and wired the enforcement path.\n`,
      { taskType: 'feature' },
    ),
    verdict: verdict({ duration: '11 min' }),
  },
];

@Component({
  selector: 'mockup-result-view-gallery',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ResultViewComponent],
  templateUrl: './result-view-gallery.component.html',
  styleUrl: './result-view-gallery.component.scss',
})
export class ResultViewGalleryComponent {
  readonly cards = CARDS;
}
