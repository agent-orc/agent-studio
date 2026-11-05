import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { sampleColours, setTheme } from '../helpers/theme';

interface Pair {
  name: string;
  foreground: string;
  background: string;
  minimum: number;
}

const text = (name: string, foreground: string, background: string): Pair =>
  ({ name, foreground, background, minimum: 4.5 });
const shape = (name: string, foreground: string, background: string): Pair =>
  ({ name, foreground, background, minimum: 3 });

const pairs: Pair[] = [
  ...['bg-surface', 'bg-elevated', 'surface-muted', 'bg-running', 'card-attention-bg']
    .map(surface => text(`fg-muted / ${surface}`, '--studio-fg-muted', `--studio-${surface}`)),
  text('live stalled copy', '--studio-live-stalled-fg', '--studio-live-stalled-bg'),
  text('pickup blocked copy', '--studio-hold-muted-fg', '--studio-hold-blocked-bg'),
  text('pickup stalled copy', '--studio-hold-muted-fg', '--studio-hold-stalled-bg'),
  text('pickup blocked mechanism', '--studio-fg-dim',
    'color-mix(in srgb, var(--studio-fg-dim) 10%, var(--studio-hold-blocked-bg))'),
  text('pickup stalled mechanism', '--studio-fg-dim',
    'color-mix(in srgb, var(--studio-fg-dim) 10%, var(--studio-hold-stalled-bg))'),
  text('pickup resolution detail', '--studio-hold-muted-fg',
    'color-mix(in srgb, var(--studio-fg-dim) 3%, var(--studio-bg-elevated))'),
  text('preparation lane title', '--lane-prep', '--studio-bg-editor'),
  text('archive lane title', '--lane-archive', '--studio-bg-editor'),
  text('progress lane pill', '--lane-progress', '--studio-bg-elevated'),
  text('auto review lane pill', '--lane-auto-review', '--studio-bg-elevated'),
  text('manual mode chip', '--studio-board-manual-fg', '--studio-board-manual-bg'),
  text('board add task', '--studio-accent-4-strong',
    'color-mix(in srgb, var(--studio-accent-4) 8%, var(--studio-bg-editor))'),
  text('pipeline passed status', '--severity-pass',
    'color-mix(in srgb, var(--severity-pass) 16%, var(--studio-bg-elevated))'),
  shape('live stalled dot', '--studio-accent-warn', '--studio-live-stalled-bg'),
  shape('live active dot', '--studio-accent-3-strong', '--studio-bg-running'),
  shape('live waiting dot', '--studio-accent-4-strong', '--studio-bg-elevated'),
  shape('pickup blocked dot', '--studio-warn', '--studio-hold-blocked-bg'),
  shape('pickup stalled dot', '--studio-accent-4-strong', '--studio-hold-stalled-bg'),
  ...(['success', 'warning', 'danger', 'info', 'running', 'neutral'] as const).flatMap(tone => {
    const pigment = `--studio-status-${tone}`;
    const compact = ['success', 'warning', 'info', 'danger'].includes(tone)
      ? `--studio-status-compact-${tone}` : pigment;
    return [
      text(`status ${tone} chip`, pigment,
        tone === 'neutral' ? '--studio-bg-surface' : `color-mix(in srgb, var(${pigment}) 4%, var(--studio-status-chip-surface))`),
      text(`status ${tone} compact / elevated`, compact, '--studio-bg-elevated'),
      text(`status ${tone} compact / stalled`, compact, '--studio-live-stalled-bg'),
      shape(`status ${tone} icon / stalled`, compact, '--studio-live-stalled-bg'),
    ];
  }),
];

test('semantic text and status shapes clear WCAG contrast in both themes', async ({ page }) => {
  await page.goto('/');
  const rows: { theme: string; token: string; ratio: number; minimum: number }[] = [];
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.evaluate((definitions) => {
      document.querySelector('#contrast-probes')?.remove();
      const root = document.createElement('div');
      root.id = 'contrast-probes';
      root.style.cssText = 'position:fixed;left:-10000px;top:0';
      for (const [index, pair] of definitions.entries()) {
        const probe = document.createElement('span');
        probe.dataset['contrastProbe'] = String(index);
        probe.textContent = 'Status';
        probe.style.color = `var(${pair.foreground})`;
        probe.style.backgroundColor = pair.background.startsWith('--')
          ? `var(${pair.background})` : pair.background;
        root.append(probe);
      }
      document.body.append(root);
    }, pairs);
    for (const [index, pair] of pairs.entries()) {
      const { color, bg } = await sampleColours(page, `[data-contrast-probe="${index}"]`);
      const ratio = contrastRatio(color, bg);
      rows.push({ theme, token: pair.name, ratio: Number(ratio.toFixed(2)), minimum: pair.minimum });
      expect.soft(ratio, `${theme}: ${pair.name} (${color} on ${bg})`).toBeGreaterThanOrEqual(pair.minimum);
    }
  }
  const dir = process.env.JOB_RESULTS_DIR ?? 'test-results';
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'contrast-ratios.json'), JSON.stringify(rows, null, 2) + '\n');
});
