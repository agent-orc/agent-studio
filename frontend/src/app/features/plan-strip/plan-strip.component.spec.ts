import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { PlanStripComponent } from './plan-strip.component';
import type { TaskPlanView } from './plan.model';

const plan: TaskPlanView = {
  hasPlan: true,
  source: 'codex/update_plan',
  snapshotCount: 4,
  activeItemId: 'patch',
  softEstimateMedian: 2,
  unassignedSubActions: [
    { ts: '2026-06-08T10:00:00Z', tool: 'Read', label: 'Read prompt.md' },
  ],
  items: [
    {
      id: 'survey',
      title: 'Survey existing progress surfaces',
      status: 'done',
      subActionCount: 2,
      subActions: [
        { ts: '2026-06-08T10:00:01Z', tool: 'Grep', label: 'Grep PlanReader' },
        { ts: '2026-06-08T10:00:02Z', tool: 'Read', label: 'Read PlanReader.cs' },
      ],
    },
    {
      id: 'patch',
      title: 'Wire plan strip into task detail',
      status: 'active',
      subActionCount: 3,
      subActions: [
        { ts: new Date().toISOString(), tool: 'Edit', label: 'Edit plan-strip.component.ts' },
        { ts: new Date().toISOString(), tool: 'Test', label: 'Test plan strip' },
        { ts: new Date().toISOString(), tool: 'Build', label: 'Build frontend' },
      ],
    },
    {
      id: 'verify',
      title: 'Verify behavior',
      status: 'pending',
      subActionCount: 0,
      subActions: [],
    },
  ],
};

async function render(input: TaskPlanView | null, isRunning = true) {
  await TestBed.configureTestingModule({
    imports: [PlanStripComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();

  const fixture = TestBed.createComponent(PlanStripComponent);
  fixture.componentRef.setInput('plan', input);
  fixture.componentRef.setInput('isRunning', isRunning);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('PlanStripComponent', () => {
  it('does not render until a plan is available', async () => {
    const fixture = await render({ ...plan, hasPlan: false, items: [] });

    expect(fixture.nativeElement.querySelector('[data-testid="plan-strip"]')).toBeNull();
  });

  it('renders plan titles, done count, active progress cue, and soft-estimate band', async () => {
    const fixture = await render(plan);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="plan-strip-source"]')?.textContent?.trim()).toBe('Codex');
    expect(host.querySelector('[data-testid="plan-strip-count"]')?.textContent?.trim()).toBe('1/3 done');
    expect(texts(host, '[data-testid="plan-item-title"]')).toEqual([
      'Survey existing progress surfaces',
      'Wire plan strip into task detail',
      'Verify behavior',
    ]);
    expect(host.querySelector('[data-testid="plan-item-latest"]')?.textContent).toContain('Build frontend');
    expect(host.querySelector('[data-testid="plan-item-band"]')?.textContent?.trim()).toBe('~2');
    expect(host.querySelector('[data-testid="plan-item-heartbeat"]')?.getAttribute('data-state')).toBe('pulsing');
    const heartbeat = host.querySelector('[data-testid="plan-item-heartbeat-indicator"]');
    expect(heartbeat?.getAttribute('data-tone')).toBe('running');
    expect(heartbeat?.textContent?.trim()).toBe('Working');
  });

  it('expands completed sub-actions so finished internal tasks stay inspectable', async () => {
    const fixture = await render(plan);
    const host = fixture.nativeElement as HTMLElement;

    host.querySelector<HTMLButtonElement>('[data-testid="plan-item-expand"]')?.click();
    fixture.detectChanges();

    expect(texts(host, '.plan-sub__label')).toContain('Grep PlanReader');
    expect(texts(host, '.plan-sub__label')).toContain('Read PlanReader.cs');
  });

  it('surfaces pre-plan work in the before-plan bucket', async () => {
    const fixture = await render(plan);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="plan-strip-before"]')?.textContent).toContain('Before plan: 1 action');
  });

  it('updates the same rows in place and briefly marks status deltas', async () => {
    const fixture = await render(plan);
    const host = fixture.nativeElement as HTMLElement;
    const before = Array.from(host.querySelectorAll('[data-testid="plan-item"]'));
    fixture.componentRef.setInput('plan', {
      ...plan,
      snapshotCount: 5,
      activeItemId: 'verify',
      items: plan.items.map(item => item.id === 'patch'
        ? { ...item, status: 'done' as const }
        : item.id === 'verify'
          ? { ...item, status: 'active' as const }
          : item),
    });
    fixture.detectChanges();

    const after = Array.from(host.querySelectorAll('[data-testid="plan-item"]'));
    expect(after).toHaveLength(3);
    expect(after[0]).toBe(before[0]);
    expect(after[1]).toBe(before[1]);
    expect(after[2]).toBe(before[2]);
    expect(after[1].getAttribute('data-delta')).toBe('changed');
    expect(after[2].getAttribute('data-delta')).toBe('changed');
  });

  it('renders a compact context block without activity drill-down telemetry', async () => {
    const fixture = await render(plan);
    fixture.componentRef.setInput('variant', 'context');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="plan-strip"]')?.getAttribute('data-variant')).toBe('context');
    expect(host.textContent).toContain('Agent progress');
    expect(host.querySelector('[data-testid="plan-item-ticker"]')).toBeNull();
    expect(host.querySelector('[data-testid="plan-strip-before"]')).toBeNull();
  });
});

function texts(host: HTMLElement, selector: string): string[] {
  return Array.from(host.querySelectorAll(selector)).map((el) => el.textContent?.trim() ?? '');
}
