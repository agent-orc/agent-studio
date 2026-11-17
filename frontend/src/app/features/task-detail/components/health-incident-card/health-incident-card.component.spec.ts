import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskDetail } from '../../../../models/task.model';
import { HealthIncidentCardComponent, incidentActionHref } from './health-incident-card.component';

const markdown = `# Health incident: Stage

- Running release: v1.4.2
- Restart count: 4
- [Open Operations](#/projects/PROJ-002/deploy?view=operations&environmentId=stage&since=2026-10-19T08%3A00%3A00Z)
- [Roll back](#/projects/PROJ-002/deploy?view=tools&environmentId=stage)
- [Run runbook](#/projects/PROJ-002/deploy?view=runbooks&environmentId=stage)

## Last crash or error log lines

- 2026-10-19 08:00 UTC error: process crashed`;

describe('HealthIncidentCardComponent', () => {
  it('renders incident evidence and operational routes without coding controls', async () => {
    await TestBed.configureTestingModule({ imports: [HealthIncidentCardComponent],
      providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(HealthIncidentCardComponent);
    fixture.componentRef.setInput('detail', {
      info: { id: 'health-stage-20261019080000', taskKey: 'NS-2', title: 'Health incident: Stage', projectName: 'demo' },
      promptMarkdown: markdown,
    } as unknown as TaskDetail);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('v1.4.2');
    expect(host.textContent).toContain('process crashed');
    expect(host.querySelector('[data-testid="incident-open-operations"]')?.getAttribute('href')).toContain('environmentId=stage');
    expect(host.querySelector('[data-testid="incident-roll-back"]')?.getAttribute('href')).toContain('view=tools');
    expect(host.querySelector('[data-testid="incident-run-runbook"]')?.getAttribute('href')).toContain('view=runbooks');
    expect(host.querySelector('[data-testid="overview-agent"]')).toBeNull();
    expect(host.querySelector('[data-testid="overview-pipeline"]')).toBeNull();
    expect(host.querySelector('[data-testid="delivery-claim-panel"]')).toBeNull();
  });

  it('does not turn unrelated markdown links into incident actions', () => {
    expect(incidentActionHref('- [Open Operations](https://external.example)', 'Open Operations')).toBeNull();
  });
});
