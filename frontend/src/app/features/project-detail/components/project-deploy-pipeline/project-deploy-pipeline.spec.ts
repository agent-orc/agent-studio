import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectDeployPipelineComponent } from './project-deploy-pipeline';
import { pipelineDefinitionFixture } from '../../testing/deploy-fixtures';

async function render(panel: unknown): Promise<HTMLElement> {
  const fixture = TestBed.createComponent(ProjectDeployPipelineComponent);
  fixture.componentRef.setInput('panel', panel);
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('ProjectDeployPipelineComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployPipelineComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
  });
  it('shows loading, unavailable, and server-error states', async () => {
    expect((await render({ status: 'loading' })).querySelector('app-loading-surface')).not.toBeNull();
    expect((await render({ status: 'unavailable' })).textContent).toContain('Pipeline definition unavailable');
    expect((await render({ status: 'error', message: 'Broken file' })).textContent).toContain('Broken file');
  });

  it('uses the AGT-3221 diagram for gate, artifacts, Staging, and Production', async () => {
    const host = await render({ status: 'ready', value: pipelineDefinitionFixture() });
    expect(host.querySelector('[data-testid="pipeline-node-gate"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="pipeline-node-artifacts"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="pipeline-node-staging"]')?.textContent).toContain('Static site');
    expect(host.querySelector('[data-testid="pipeline-node-production"]')?.textContent).toContain('Compose service');
  });

  it('shows the parsed diagram beside validation issues on an invalid file', async () => {
    const value = { ...pipelineDefinitionFixture(), valid: false, issues: [
      { path: 'deploy[0]', code: 'invalid-step', message: 'Invalid step', line: 12 },
    ] };
    const host = await render({ status: 'ready', value });
    expect(host.querySelector('[data-testid="deploy-pipeline-issues"]')?.textContent).toContain('Invalid step');
    expect(host.querySelector('[data-testid="pipeline-diagram"]')).not.toBeNull();
  });
});
