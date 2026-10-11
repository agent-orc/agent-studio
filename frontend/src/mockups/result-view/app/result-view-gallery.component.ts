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
  {
    heading: 'assessment - AGT-2798 contradicting signals (AGT-3046)',
    detail: {
      ...detail(
        `# Status\n\n- Result: NeedsInput\n- Case: blocked\n- Duration: 49 min\n- Files: 6\n\n## Overview\n- Problem: The North star Dossier was outdated.\n- Solution: Rewrote it; two operator decisions stay open.\n`,
        { taskType: 'planning', mode: 'concept', commits: [], codeActivityDetected: false },
      ),
      summaryAssessment: {
        version: 1,
        outcome: 'delivered-via-operator-merge',
        outcomeReason: 'No card commit carries the work; operator merge `5d480157f` on `main` delivered it.',
        policyOutcome: 'delivered-via-operator-merge',
        source: 'policy',
        fallbackReason: null,
        model: 'claude-opus-5-5',
        generatedAt: '2026-10-11T12:00:00Z',
        inconsistencies: [
          {
            kind: 'files-vs-no-code',
            signalA: 'status.md header: Files: 6',
            signalB: 'commit chain is empty and no code activity was detected ("no code change")',
            explanation: 'The run\'s changes did not land as card commits. Operator merge `5d480157f` on `main` carried them (salvage commit `7aa00e27f`). The file count describes that work; "no code change" is a commit-attribution gap, not lost work.',
            workMayBeLost: false,
          },
          {
            kind: 'failure-vs-completed',
            signalA: 'Pipeline failure: `post-integrate-merge` failed (run parked on NeedsInput; no publishable delivery commit)',
            signalB: 'lane `6-completed` with completion claim `operator-override`',
            explanation: 'The automated run did not finish delivery; the card was completed afterwards through operator merge `5d480157f` on `main` (salvage commit `7aa00e27f`).',
            workMayBeLost: false,
          },
          {
            kind: 'salvage-not-integrated',
            signalA: 'salvage commit `0badc0de1` (illustrative possible-loss row)',
            signalB: 'no card commit and no integration record',
            explanation: 'Nothing on the card integrates it, so it is not on the delivery branch.',
            workMayBeLost: true,
          },
        ],
      },
    } as TaskDetail,
    verdict: verdict({ kind: 'problem', status: 'failed', label: 'Pipeline failure', emoji: '🔴', duration: '49 min' }),
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
