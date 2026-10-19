import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployPipelineComponent } from './project-deploy-pipeline';
import type { DeployPanel } from '../../state/deploy-area.model';
import { pipelineFromDefinition, pipelineFromExecution, type DeployPipelineView } from '../../state/deploy-pipeline.model';
import { executionLifecycleFixture, invalidExecutionFixture, pipelineDefinitionFixture } from '../../testing/deploy-fixtures';

describe('ProjectDeployPipelineComponent', () => {
  let fixture: ComponentFixture<ProjectDeployPipelineComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const text = (id: string) => byTestId(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  /** A step renders its kind and each detail as separate lines. */
  const stepText = (id: string) => Array.from(byTestId(id)?.children ?? []).map(item => item.textContent?.trim()).join(' | ');

  async function render(panel: DeployPanel<DeployPipelineView>): Promise<void> {
    fixture.componentRef.setInput('panel', panel);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectDeployPipelineComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectDeployPipelineComponent);
  });

  it('shows the loading surface and the error state with the server message', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();

    await render({ status: 'error', message: 'Project not found.' });
    expect(text('deploy-pipeline-error')).toContain('Project not found.');
  });

  it('draws gate, artifact, Staging, and Production in order with their rules', async () => {
    await render({ status: 'ready', value: pipelineFromExecution(executionLifecycleFixture()) });

    const stages = Array.from(host().querySelectorAll('.pipeline__node')).map(node => node.getAttribute('data-testid'));
    expect(stages).toEqual(['deploy-stage-gate', 'deploy-stage-artifact', 'deploy-stage-staging', 'deploy-stage-production']);
    expect(text('deploy-stage-gate')).toContain('npm run build');
    expect(text('deploy-stage-staging')).toContain('Automatic');
    expect(text('deploy-stage-production')).toContain('Approval: Owner or Maintainer');
    expect(text('deploy-stage-production')).toContain('After staging');
    expect(stepText('deploy-step-staging-0')).toBe('Publish static | web to web-vm | deploy/website');
    expect(stepText('deploy-step-staging-1')).toBe('Health check | Health /healthz');
    expect(byTestId('deploy-pipeline-issues')).toBeNull();
  });

  it('shows static-site and container steps with target, destination, and health from the AGT-3221 diagram', async () => {
    await render({ status: 'ready', value: pipelineFromDefinition(pipelineDefinitionFixture()) });

    expect(stepText('deploy-step-staging-0')).toBe('Static site | web to web-vm | /srv/www/website | Health /healthz within 60 s');
    expect(stepText('deploy-step-production-1')).toBe('Container | api to app-vm | /srv/api/compose.yml (api) | Health /healthz within 60 s');
    expect(text('deploy-stage-artifact')).toContain('api (container, Dockerfile, registry.example/website-api)');
  });

  it('shows the parsed diagram of an invalid project.yml below its validation issues', async () => {
    await render({ status: 'ready', value: pipelineFromExecution(invalidExecutionFixture()) });

    expect(text('deploy-pipeline-issues')).toContain('project.yml has 1 validation issue');
    expect(text('deploy-pipeline-issues')).toContain('deploy[0].step');
    expect(text('deploy-pipeline-issues')).toContain('The diagram below shows the file as parsed');
    expect(stepText('deploy-step-staging-0')).toBe('Static site | web to web-vm');
    expect(byTestId('deploy-pipeline-undeclared')).toBeNull();
  });

  it('names the issue line when the source reports one, and says when nothing could be parsed', async () => {
    await render({ status: 'ready', value: { valid: false, nodes: null,
      issues: [{ path: '$', message: 'Repository execution definition is missing.', line: null },
        { path: 'deploy[1]', message: 'Unknown key.', line: 12 }] } });

    expect(text('deploy-pipeline-issues')).toContain('No diagram could be parsed from the file.');
    expect(text('deploy-pipeline-issues')).toContain('deploy[1]:12 Unknown key.');
    expect(host().querySelector('.pipeline__flow')).toBeNull();
  });

  it('invites a declaration when project.yml has no deploy lifecycle', async () => {
    await render({ status: 'ready', value: { valid: true, issues: [], nodes: [] } });
    expect(text('deploy-pipeline-undeclared')).toContain('No deploy lifecycle declared');
  });
});
