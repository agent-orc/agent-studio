/**
 * Research mode uses the real lightweight report pipeline.
 *
 * This is a billable proof, not a mocked catalogue check. The dev-backend
 * fixture creates an isolated task repository, a real Research card runs a
 * bounded Codex authoring turn, and the assertions read the resulting report
 * and pipeline execution back through the public API. The screenshot proves
 * the operator-facing pipeline view contains no code gates. A dirty source
 * checkout may stop at read-only containment after the core run; that guard is
 * orthogonal to the pipeline-selection proof.
 */
import { test, expect } from '../fixtures/dev-backend';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const CLIENT_ID = 'local-default';
const RESULTS_DIR = resolve(process.cwd(), '..', 'results', 'AGT-2417');

interface WatchPath {
  path: string;
}

interface PipelineStep {
  id: string;
  displayName: string;
}

interface PipelineResponse {
  pipeline: {
    id: string;
    displayName: string;
    allSteps: PipelineStep[];
  };
  execution: {
    completedAt?: string | null;
    steps?: { stepId: string; status: string }[];
  } | null;
}

async function request(
  baseUrl: string,
  path: string,
  init: RequestInit = {},
): Promise<Response> {
  return fetch(`${baseUrl}${path}`, {
    ...init,
    headers: {
      'X-Client-Id': CLIENT_ID,
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init.headers ?? {}),
    },
  });
}

test.describe('Research lightweight pipeline @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(300_000);

  test('a real Research card runs without build, Stylelint, aspects, or code-quality steps', async ({
    page,
    devBackend,
  }) => {
    const pathsResponse = await request(devBackend.baseUrl, '/api/watch-paths');
    expect(pathsResponse.ok).toBe(true);
    const paths = await pathsResponse.json() as WatchPath[];
    expect(paths.length).toBeGreaterThan(0);
    const watchPath = paths[0].path;
    const id = `research-lightweight-proof-${Date.now()}`;
    const watchQuery = `watchPath=${encodeURIComponent(watchPath)}`;

    const create = await request(devBackend.baseUrl, '/api/tasks', {
      method: 'POST',
      body: JSON.stringify({
        id,
        title: 'Research: lightweight pipeline proof',
        watchPath,
        targetState: '2-ready',
        taskType: 'chore',
        mode: 'research',
        allowWebAccess: false,
        agent: 'codex',
        cliType: 'codex',
        model: 'gpt-5.6-luna',
        thinkingLevel: 'medium',
        fixture: true,
        promptMarkdown: [
          '# Research: lightweight pipeline proof',
          '',
          'Create exactly one self-contained HTML report at `results/report.html`.',
          'The report must have an English title, one sentence confirming that this is a real Research pipeline run, inline CSS, and no external dependencies.',
          'This is a bounded artifact probe: do not inspect the repository or gather additional evidence.',
          'Do not modify the product checkout. End with `[[TASK_DONE]]`.',
        ].join('\n'),
      }),
    });
    expect(create.ok).toBe(true);

    try {
      const start = await request(
        devBackend.baseUrl,
        `/api/tasks/${encodeURIComponent(id)}/start?${watchQuery}`,
        {
          method: 'POST',
          body: JSON.stringify({
            cliType: 'codex',
            model: 'gpt-5.6-luna',
            thinkingLevel: 'medium',
          }),
        },
      );
      expect([200, 202]).toContain(start.status);

      await expect.poll(async () => {
        const response = await request(
          devBackend.baseUrl,
          `/api/tasks/${encodeURIComponent(id)}/results/report.html?${watchQuery}`,
        );
        return response.status;
      }, {
        timeout: 240_000,
        intervals: [1_000, 2_000, 5_000],
      }).toBe(200);

      const report = await request(
        devBackend.baseUrl,
        `/api/tasks/${encodeURIComponent(id)}/results/report.html?${watchQuery}`,
      );
      expect(report.ok).toBe(true);
      expect(await report.text()).toContain('<html');

      await expect.poll(async () => {
        const response = await request(
          devBackend.baseUrl,
          `/api/tasks/${encodeURIComponent(id)}/pipeline?${watchQuery}`,
        );
        if (!response.ok) return 'not-found';
        const current = await response.json() as PipelineResponse;
        return current.execution?.steps?.find(
          step => step.stepId === 'core-agent-run',
        )?.status ?? 'missing';
      }, {
        timeout: 60_000,
        intervals: [500, 1_000, 2_000],
      }).toBe('passed');

      const pipelineResponse = await request(
        devBackend.baseUrl,
        `/api/tasks/${encodeURIComponent(id)}/pipeline?${watchQuery}`,
      );
      expect(pipelineResponse.ok).toBe(true);
      const pipeline = await pipelineResponse.json() as PipelineResponse;
      expect(pipeline.pipeline.id).toBe('read-only-task-pipeline');
      expect(pipeline.pipeline.displayName).toBe('Lightweight report pipeline');

      const stepIds = pipeline.pipeline.allSteps.map(step => step.id);
      expect(stepIds).toEqual([
        'pre-loop-guard',
        'pre-model-qualification',
        'pre-prompt-enrichment',
        'pre-orchestrator-prep',
        'pre-reissue-open-items',
        'core-agent-run',
        'post-summary-assessment',
        'aspect-requirement-fit',
        'post-orchestrator-review',
        'post-orchestrator-decision',
      ]);
      expect(stepIds.some(step => step.includes('build'))).toBe(false);
      expect(stepIds.some(step => step.includes('lint'))).toBe(false);
      // AGT-3565: the report gets only its requirement-fit content verdict.
      expect(stepIds.filter(step => step.startsWith('aspect-'))).toEqual(['aspect-requirement-fit']);
      expect(stepIds.some(step => step.includes('code-review'))).toBe(false);

      await page.addInitScript(() => {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
      });
      await page.goto(
        `/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}&includeFixtures=true`,
      );
      const leaveUncommitted = page.getByTestId('crash-recovery-dismiss').first();
      await leaveUncommitted.waitFor({ state: 'visible', timeout: 5_000 }).catch(() => undefined);
      if (await leaveUncommitted.isVisible().catch(() => false)) {
        await leaveUncommitted.click();
      }
      const pipelineView = page.getByTestId('overview-pipeline');
      await expect(pipelineView).toBeVisible({ timeout: 20_000 });
      const visibleSteps = await pipelineView
        .getByTestId('overview-pipeline-step-name')
        .allTextContents();
      expect(visibleSteps.join(' ')).not.toMatch(/build|stylelint|code quality|requirement fit/i);

      await mkdir(RESULTS_DIR, { recursive: true });
      await pipelineView.screenshot({
        path: resolve(RESULTS_DIR, 'research-lightweight-pipeline.png'),
      });
    } finally {
      await request(
        devBackend.baseUrl,
        `/api/tasks/${encodeURIComponent(id)}?${watchQuery}`,
        { method: 'DELETE' },
      );
    }
  });
});
