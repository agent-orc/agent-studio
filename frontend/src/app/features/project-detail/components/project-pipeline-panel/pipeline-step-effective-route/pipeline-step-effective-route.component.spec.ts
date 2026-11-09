import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import type { EffectiveModelSelection } from '../../../../quota';
import { PipelineStepEffectiveRouteComponent } from './pipeline-step-effective-route.component';
import { effectiveRouteView } from './pipeline-step-effective-route.util';

function route(overrides: Partial<EffectiveModelSelection> = {}): EffectiveModelSelection {
  return {
    consumer: 'review-aspect', stepId: 'aspect-code-quality',
    configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
    effective: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
    rule: 'quota-cap', reason: 'Claude weekly window at 97 %, above the 95 % cap.',
    ...overrides,
  };
}

describe('effectiveRouteView', () => {
  it('returns null when the effective route matches the configured CLI and model', () => {
    const same = route({ effective: { cliType: 'Claude', model: 'CLAUDE-OPUS-5', thinkingLevel: 'low' } });
    expect(effectiveRouteView(same, 'claude', 'claude-opus-5')).toBeNull();
    expect(effectiveRouteView(null, 'claude', 'claude-opus-5')).toBeNull();
  });

  it('describes a quota fallback with its rule and recorded reason', () => {
    const view = effectiveRouteView(route(), 'claude', 'claude-opus-5');
    expect(view).toMatchObject({
      lead: 'Runs', route: 'codex · gpt-6-sol', configured: 'claude · claude-opus-5',
      ruleLabel: 'Quota cap', tone: 'warning', reasonRecorded: true,
    });
    expect(view?.detail).toContain('Reason: Claude weekly window');
  });

  it('shows a missing reason as "reason not recorded" instead of hiding it', () => {
    const view = effectiveRouteView(route({ rule: 'consumer-default', reason: null }), 'claude', 'claude-opus-5');
    expect(view?.reason).toBe('reason not recorded');
    expect(view?.reasonRecorded).toBe(false);
    expect(view?.ruleLabel).toBe('Consumer policy');
  });

  it('compares against the row value, falling back to the server configured route when blank', () => {
    expect(effectiveRouteView(route({ effective: route().configured }), '', '')).toBeNull();
    expect(effectiveRouteView(route({ effective: route().configured }), 'claude', 'claude-sonnet-5')?.route)
      .toBe('claude · claude-opus-5');
  });

  it('reports a launch that would wait for quota', () => {
    const view = effectiveRouteView(route({ effective: null }), 'claude', 'claude-opus-5');
    expect(view).toMatchObject({ lead: 'Waits', route: 'no route admitted', tone: 'warning' });
  });
});

describe('PipelineStepEffectiveRouteComponent', () => {
  async function render(value: EffectiveModelSelection | null): Promise<HTMLElement> {
    await TestBed.configureTestingModule({
      imports: [PipelineStepEffectiveRouteComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(PipelineStepEffectiveRouteComponent);
    fixture.componentRef.setInput('stepId', 'aspect-code-quality');
    fixture.componentRef.setInput('route', value);
    fixture.componentRef.setInput('configuredCli', 'claude');
    fixture.componentRef.setInput('configuredModel', 'claude-opus-5');
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement;
  }

  it('renders the effective route, configured route, rule, and placeholder reason', async () => {
    const host = await render(route({ reason: null }));
    const line = host.querySelector('[data-testid="pipeline-step-effective-aspect-code-quality"]');
    expect(line?.getAttribute('data-rule')).toBe('quota-cap');
    expect(line?.textContent).toContain('Runs codex · gpt-6-sol');
    expect(line?.textContent).toContain('configured claude · claude-opus-5');
    expect(host.querySelector('[data-testid="pipeline-step-effective-rule-aspect-code-quality"]')?.textContent).toContain('Quota cap');
    expect(host.querySelector('[data-testid="pipeline-step-effective-reason-aspect-code-quality"]')?.textContent)
      .toContain('reason not recorded');
    expect(host.classList.contains('is-shown')).toBe(true);
  });

  it('renders nothing when the routes agree', async () => {
    const host = await render(route({ effective: route().configured }));
    expect(host.querySelector('[data-testid="pipeline-step-effective-aspect-code-quality"]')).toBeNull();
    expect(host.classList.contains('is-shown')).toBe(false);
  });
});
