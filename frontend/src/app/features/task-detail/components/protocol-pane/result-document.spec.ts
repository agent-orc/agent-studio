import { describe, expect, it } from 'vitest';
import type { TaskDetail } from '../../../../models/task.model';
import type { ProtocolVerdict } from './protocol-verdict';
import {
  buildResultDocument,
  classifyTestsMetric,
  codeReviewGradeFromTags,
  compactDurationMetric,
  parseCaseHint,
  parseHeaderMetric,
} from './result-document';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    status: 'succeeded',
    signals: [],
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    lane: null,
    duration: null,
    ...overrides,
  };
}

interface DetailOpts {
  statusMarkdown?: string | null;
  title?: string;
  taskType?: string | null;
  mode?: string | null;
  tags?: string[];
  totalTokens?: number;
  commits?: number;
  codeActivityDetected?: boolean;
}

function detail(opts: DetailOpts = {}): TaskDetail {
  return {
    info: {
      id: 'job-1',
      watchPath: '/ws',
      title: opts.title ?? 'Fix the broken thing',
      taskType: opts.taskType ?? 'chore',
      mode: opts.mode ?? 'coding',
      tags: opts.tags ?? [],
      tokenSummary: opts.totalTokens != null ? ({
        totalTokens: opts.totalTokens,
        estimatedApiCostUsd: 1.25,
        allModelsPriced: true,
      } as never) : null,
      commits: opts.commits != null ? new Array(opts.commits).fill({}) : [],
      codeActivityDetected: opts.codeActivityDetected,
    },
    statusMarkdown: opts.statusMarkdown ?? null,
  } as unknown as TaskDetail;
}

const STATUS_WITH_OVERVIEW = `# Status

- Result: Success
- Duration: 4 min

## Overview
- Problem: The verdict banner was unreadable on the light theme.
- Solution: Re-toned it to a neutral surface with a semantic accent stripe.

## What Was Done
- Edited \`banner.scss\` to use tokens.
- Added a regression spec.

## Open Items
- Wire the same tokens into the dark theme sweep.
- None.

## Images
- ![](results/banner--real.png)
`;

const STATUS_LEGACY = `# Status

- Result: Success
- Duration: 2 min

## What Was Done
- Renamed the helper and deduplicated the call sites.

## Open Items
- None.
`;

const STATUS_SCAFFOLD = `<!-- agent-studio:result-scaffold -->
<!-- agent-studio:operator-result-backfill -->
# Status

- Result: Success
- Case: generic
- Grade: Not recorded
- Deliverables: [results/report.html](results/report.html)
- Integration: \`no-branch\` on \`develop\`
- Provenance: Synthesized by Agent Studio after entering \`5-human-review\` because no generated status.md was available.

## Overview

- Problem: \`status.md\` was missing when task \`C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::raw-job-id\` reached \`5-human-review\`.
- Solution: This honest scaffold exposes the recorded outcome and existing evidence for a planning task.

## What Was Done

- The task reached \`5-human-review\`.

## Open Items

- None recorded in this synthesized scaffold.
`;

describe('codeReviewGradeFromTags', () => {
  it('reads the A-D grade from the code-review grade tag', () => {
    expect(codeReviewGradeFromTags(['x', 'code-review:grade-b', 'y'])).toBe('B');
    expect(codeReviewGradeFromTags(['CODE-REVIEW:GRADE-A'])).toBe('A');
  });
  it('returns null without a grade tag', () => {
    expect(codeReviewGradeFromTags([])).toBeNull();
    expect(codeReviewGradeFromTags(['area:frontend'])).toBeNull();
    expect(codeReviewGradeFromTags(null)).toBeNull();
  });
});

describe('parseCaseHint', () => {
  it('pulls a `- Case:` line out of the markdown', () => {
    expect(parseCaseHint('# Status\n\n- Case: ui-cleanup\n\n## Overview')).toBe('ui-cleanup');
  });
  it('returns null when absent', () => {
    expect(parseCaseHint('# Status\n\n## Overview')).toBeNull();
    expect(parseCaseHint(null)).toBeNull();
  });
});

describe('parseHeaderMetric', () => {
  it('reads a header metric line from the # Status block', () => {
    const md = '# Status\n\n- Result: Success\n- Files: 5\n- Tests: 12 passed\n\n## Overview';
    expect(parseHeaderMetric(md, 'Files')).toBe('5');
    expect(parseHeaderMetric(md, 'Tests')).toBe('12 passed');
  });
  it('stops at the first H2 so a body bullet cannot masquerade as a head metric', () => {
    const md = '# Status\n\n- Result: Success\n\n## What Was Done\n- Files: touched everything\n';
    expect(parseHeaderMetric(md, 'Files')).toBeNull();
  });
  it('returns null for an absent or empty value', () => {
    expect(parseHeaderMetric('# Status\n- Result: Success', 'Files')).toBeNull();
    expect(parseHeaderMetric('# Status\n- Files:   ', 'Files')).toBeNull();
    expect(parseHeaderMetric(null, 'Files')).toBeNull();
  });
});

describe('classifyTestsMetric', () => {
  it('reads an X/Y tally and warns when some failed', () => {
    expect(classifyTestsMetric('11/12 passed')).toEqual({ value: '11/12', tone: 'warn' });
    expect(classifyTestsMetric('12/12 passed')).toEqual({ value: '12/12 ✓', tone: 'ok' });
  });
  it('tones a bare pass green and a failure red', () => {
    expect(classifyTestsMetric('12 passed')).toEqual({ value: '12 ✓', tone: 'ok' });
    expect(classifyTestsMetric('2 failed').tone).toBe('problem');
  });
});

describe('compactDurationMetric', () => {
  it('compacts common time units while preserving compound values', () => {
    expect(compactDurationMetric('20 min')).toBe('20m');
    expect(compactDurationMetric('1 hour 8 minutes')).toBe('1h 8m');
    expect(compactDurationMetric('45 sec')).toBe('45s');
  });
});

describe('buildResultDocument', () => {
  it('parses an explicit ## Overview into problem/solution (not synthesized)', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_WITH_OVERVIEW }), verdict({ duration: '4 min' }));
    expect(doc.overview.synthesized).toBe(false);
    expect(doc.overview.problem).toContain('unreadable on the light theme');
    expect(doc.overview.solution).toContain('neutral surface');
  });

  it('synthesizes an overview from title + first What Was Done bullet on legacy status.md', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_LEGACY, title: 'Dedup helper' }), verdict());
    expect(doc.overview.synthesized).toBe(true);
    expect(doc.overview.problem).toBe('Dedup helper');
    expect(doc.overview.solution).toContain('Renamed the helper');
  });

  it('strips the # Status and ## Overview blocks from the detail markdown', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_WITH_OVERVIEW }), verdict({ duration: '4 min' }));
    expect(doc.detailMarkdown).not.toContain('# Status');
    expect(doc.detailMarkdown).not.toContain('## Overview');
    expect(doc.detailMarkdown).not.toContain('unreadable on the light theme');
    // Everything below the overview survives.
    expect(doc.detailMarkdown).toContain('## What Was Done');
    expect(doc.detailMarkdown).toContain('## Images');
  });

  it('does not repeat the authoritative case verdict as a metric', () => {
    const doc = buildResultDocument(detail(), verdict({ kind: 'problem', label: 'Blocked', emoji: '🔴' }));
    expect(doc.metrics.some((x) => x.id === 'verdict')).toBe(false);
  });

  it('adds a grade stat from the code-review tag', () => {
    const doc = buildResultDocument(detail({ tags: ['code-review:grade-a'] }), verdict());
    const grade = doc.metrics.find((x) => x.id === 'grade');
    expect(grade?.value).toBe('Grade A');
    expect(grade?.tone).toBe('ok');
  });

  it('adds compact duration + token + commit stats when the data is present', () => {
    const doc = buildResultDocument(
      detail({ totalTokens: 1_500_000, commits: 2 }),
      verdict({ duration: '4 min' }),
    );
    expect(doc.metrics.find((x) => x.id === 'duration')?.value).toBe('4m');
    const tokens = doc.metrics.find((x) => x.id === 'tokens');
    expect(tokens?.value).toBe('1.50M tokens');
    expect(tokens?.tooltip).toContain('Estimated cost: $1.25');
    expect(tokens?.tooltip).toContain('historical list prices');
    expect(doc.metrics.find((x) => x.id === 'commits')?.value).toBe('2 commits');
  });

  it('adds files + tests stats from the # Status header lines', () => {
    const md = `# Status\n\n- Result: Success\n- Files: 3\n- Tests: 11/12 passed\n\n## What Was Done\n- Did work.\n`;
    const doc = buildResultDocument(detail({ statusMarkdown: md }), verdict());
    expect(doc.metrics.find((x) => x.id === 'files')?.value).toBe('3 files');
    const tests = doc.metrics.find((x) => x.id === 'tests');
    expect(tests?.value).toBe('11/12');
    expect(tests?.tone).toBe('warn');
  });

  it('omits the files + tests stats when the header carries no metric lines', () => {
    const doc = buildResultDocument(detail({ statusMarkdown: STATUS_LEGACY }), verdict());
    expect(doc.metrics.find((x) => x.id === 'files')).toBeUndefined();
    expect(doc.metrics.find((x) => x.id === 'tests')).toBeUndefined();
  });

  it('renders a "no code change" commit stat when the scanner saw no activity', () => {
    const doc = buildResultDocument(detail({ commits: 0, codeActivityDetected: false }), verdict());
    expect(doc.metrics.find((x) => x.id === 'commits')?.value).toBe('no code change');
  });

  it('classifies the case from metadata (bug task -> bugfix)', () => {
    const doc = buildResultDocument(detail({ taskType: 'bug', statusMarkdown: STATUS_LEGACY }), verdict());
    expect(doc.case.case).toBe('bugfix');
  });

  it('leads with the blocked case when the verdict is a problem', () => {
    const doc = buildResultDocument(detail({ taskType: 'feature' }), verdict({ kind: 'problem', label: 'Blocked' }));
    expect(doc.case.case).toBe('blocked');
  });

  it('projects a marked transition scaffold as compact provenance without internal overview text', () => {
    const d = detail({ statusMarkdown: STATUS_SCAFFOLD, title: 'Planning task' });
    Object.assign(d.info, {
      key: 'AGT-2514',
      taskKey: 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard::raw-job-id',
      lastActivity: '2026-08-08T21:59:26.180Z',
    });

    const doc = buildResultDocument(d, verdict());

    expect(doc.overview).toEqual({ problem: null, solution: null, synthesized: true });
    expect(doc.detailMarkdown).toContain('## What Was Done');
    expect(doc.detailMarkdown).toContain('## Open Items');
    expect(doc.detailMarkdown).not.toContain('agent-studio:result-scaffold');
    expect(doc.detailMarkdown).not.toContain('agent-studio:operator-result-backfill');
    expect(doc.detailMarkdown).not.toContain('C:\\Projects');
    expect(doc.detailMarkdown).not.toContain('This honest scaffold exposes');
  });
});
