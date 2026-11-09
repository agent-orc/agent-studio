import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectPipelinePanelComponent } from './project-pipeline-panel.component';
import type { PipelineCatalogueStep } from '../../../task-pipeline';

function aspect(id: string): PipelineCatalogueStep {
  return {
    id, displayName: id, kind: 'aspect', phase: 'aspect', runMode: 'parallel', dependsOn: [],
    idempotent: true, stub: false, resolvedModel: 'claude-opus-5', modelSource: 'runtime',
    usesModel: true, usesPrompt: true, supportsMode: false, cliType: 'claude', promptTemplate: id,
    canDisable: true, defaultEnabled: true, supportsCondition: true,
  };
}

async function setup() {
  await TestBed.configureTestingModule({
    imports: [ProjectPipelinePanelComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectPipelinePanelComponent);
  fixture.componentRef.setInput('projectName', 'demo');
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  fixture.componentInstance.catalogue.set([aspect('aspect-code-quality'), aspect('aspect-security')]);
  return { fixture, http, host: fixture.nativeElement as HTMLElement };
}

describe('ProjectPipelinePanelComponent effective routes', () => {
  it('loads the project-scoped effective routes and shows a configured-versus-effective mismatch', async () => {
    const { fixture, http, host } = await setup();
    const request = http.expectOne(r => r.url === '/api/model-selection/effective');
    expect(request.request.params.get('project')).toBe('demo');
    expect(request.request.params.get('pipelineType')).toBe('task');
    request.flush({ routes: [
      { consumer: 'review-aspect', stepId: 'aspect-code-quality',
        configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
        effective: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' }, rule: 'quota-cap', reason: null },
      { consumer: 'review-aspect', stepId: 'aspect-security',
        configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
        effective: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' }, rule: 'consumer-default', reason: null },
    ] });
    fixture.detectChanges();
    await fixture.whenStable();
    const mismatch = host.querySelector('[data-testid="pipeline-step-effective-aspect-code-quality"]');
    expect(mismatch?.textContent).toContain('Runs codex · gpt-6-sol');
    expect(mismatch?.textContent).toContain('Quota cap');
    expect(mismatch?.textContent).toContain('reason not recorded');
    expect(host.querySelector('[data-testid="pipeline-step-effective-aspect-security"]')).toBeNull();
    expect(host.querySelector('[data-testid="pipeline-group-aspect"] [data-testid="aspect-model-bulk-apply"]')).toBeTruthy();
  });

  it('applies one route to every aspect step with sequential step writes', async () => {
    const { fixture, http } = await setup();
    let done = false;
    fixture.componentInstance.applyAspectAgent({ cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' })
      .subscribe({ complete: () => { done = true; } });
    const first = http.expectOne('/api/projects/demo/pipeline-step');
    expect(first.request.body).toMatchObject({ pipelineType: 'task', stepId: 'aspect-code-quality',
      cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high', economyModel: null });
    first.flush({ pipelineSteps: { 'aspect-code-quality': { cliType: 'codex', model: 'gpt-6-sol' } } });
    const second = http.expectOne('/api/projects/demo/pipeline-step');
    expect(second.request.body).toMatchObject({ stepId: 'aspect-security', model: 'gpt-6-sol' });
    second.flush({ pipelineSteps: { 'aspect-code-quality': { model: 'gpt-6-sol' }, 'aspect-security': { model: 'gpt-6-sol' } } });
    expect(done).toBe(true);
    expect(Object.keys(fixture.componentInstance.overrides())).toEqual(['aspect-code-quality', 'aspect-security']);
    expect(http.match(r => r.url === '/api/model-selection/effective').length).toBeGreaterThanOrEqual(2);
  });
});
