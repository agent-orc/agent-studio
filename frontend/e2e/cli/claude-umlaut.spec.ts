import { test, expect } from '@playwright/test';
import { createJob, startJob, waitForJob, getJobOutput } from '../helpers/jobs';
import { api, BACKEND } from '../helpers/api';

/**
 * Regression: starting a Claude task with a German-umlaut prompt and Sonnet 4.6
 * via the detail-view dropdown used to silently kill the whole API process.
 * Two suspected root causes — both addressed:
 *   1. CLI stdout/stderr were read with the system code page (CP1252), causing
 *      a decoder fallback throw on multibyte UTF-8 from the Claude CLI.
 *   2. Event subscribers (SignalR fan-out, etc.) could throw inside fire-and-
 *      forget Tasks; that bubbled to TaskScheduler.UnobservedTaskException
 *      and terminated the host.
 *
 * The fix forces UTF-8 on the redirected streams, wraps event invocations in
 * try/catch, and installs an UnobservedTaskException safety net in Program.cs.
 *
 * Marked @billable — uses real Claude quota (Sonnet on a tiny prompt).
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

test.describe('Claude Code — umlaut prompt with Sonnet 4.6 @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(240_000);

  test('handles umlauts + Sonnet without crashing the API', async () => {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const title = `e2e Umlauts ÄÖÜß ${stamp}`;
    const created = await createJob({
      title,
      watchPath: WATCH_PATH,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-sonnet-4-6',
      promptMarkdown:
        'Antworte auf Deutsch mit genau diesem Text und nichts weiter: "Hallöchen, Übermüdung über Öl". Keine Datei-Edits.',
      targetState: '2-ready'
    });

    const exec = await startJob(created.id, WATCH_PATH, {
      cliType: 'claude',
      model: 'claude-sonnet-4-6'
    });
    expect(exec.status).toBe('running');

    const finished = await waitForJob(
      created.id,
      WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 200_000, intervalMs: 2_000 }
    );

    // Smoke: API still alive after the run.
    const health = await fetch(`${BACKEND}/healthz`);
    expect(health.ok, 'API must still be reachable after umlaut run').toBe(true);

    expect(finished.execution).not.toBeNull();
    const e = finished.execution!;
    expect(
      e.status,
      `umlaut+Sonnet run failed: status=${e.status} exit=${e.exitCode} dur=${e.durationSeconds}s`
    ).toBe('completed');
    expect(e.exitCode).toBe(0);

    // Encoding regression: a CP1252 decode of UTF-8 bytes produces either
    // the U+FFFD replacement char or specific Mojibake patterns ("Ã¤" for "ä"
    // etc.). The actual model reply varies, so we don't assert specific text —
    // we assert that whatever came back is not corrupted. Whether the model
    // chose to *use* umlauts in its reply is irrelevant.
    const out = await getJobOutput(created.id, WATCH_PATH);
    const text = JSON.stringify(out);
    expect(text, 'output must not contain U+FFFD replacement char').not.toMatch(/�/);
    expect(text, 'output must not contain CP1252→UTF8 mojibake').not.toMatch(/Ã[¤¶¼„–œŸ]/);
  });

  test('Sonnet 4.6 model id is exposed in the catalog', async () => {
    const cat = await api<{ models: Array<{ id: string }> }>('/api/cli/claude/models');
    expect(cat.models.map(m => m.id)).toContain('claude-sonnet-4-6');
  });
});
