import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ProjectDeployPipelineComponent } from './project-deploy-pipeline';
import type { DeployLifecycleRead, DeployPanel } from '../../state/deploy-area.model';
import { deployLifecycleFixture } from '../../testing/deploy-fixtures';

describe('ProjectDeployPipelineComponent', () => {
  let fixture: ComponentFixture<ProjectDeployPipelineComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function render(panel: DeployPanel<DeployLifecycleRead>): Promise<void> {
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

  it('shows the loading surface and the error state', async () => {
    await render({ status: 'loading' });
    expect(host().querySelector('app-loading-surface')).not.toBeNull();

    await render({ status: 'error', message: 'The project.yml lifecycle could not be loaded.' });
    expect(byTestId('deploy-pipeline-error')).not.toBeNull();
  });

  it('asks to fix an invalid project.yml before drawing', async () => {
    await render({ status: 'ready', value: { valid: false, lifecycle: null } });
    expect(byTestId('deploy-pipeline-invalid')?.textContent).toContain('validation issues');
  });

  it('explains which sections declare a lifecycle when none is declared', async () => {
    await render({ status: 'ready', value: { valid: true, lifecycle: { declared: false, nodes: [] } } });
    expect(byTestId('deploy-pipeline-undeclared')?.textContent).toContain('lifecycles.standard');

    await render({ status: 'ready', value: { valid: true } });
    expect(byTestId('deploy-pipeline-undeclared')).not.toBeNull();
  });

  it('draws gate, artifact, Staging, and Production in order with their steps and rules', async () => {
    await render({ status: 'ready', value: deployLifecycleFixture() });

    const nodes = Array.from(host().querySelectorAll('.pipeline__node')).map(node => node.getAttribute('data-testid'));
    expect(nodes).toEqual(['deploy-stage-gate', 'deploy-stage-artifact', 'deploy-stage-staging', 'deploy-stage-production']);
    expect(byTestId('deploy-stage-gate')?.textContent).toContain('npm run build');
    expect(byTestId('deploy-stage-staging')?.textContent).toContain('Automatic');
    expect(byTestId('deploy-stage-production')?.textContent).toContain('Approval: Owner or Maintainer');
    expect(byTestId('deploy-stage-production')?.textContent).toContain('After staging');
    expect(host().querySelector('button, input, select')).toBeNull();
  });

  it('names a node without items instead of leaving it blank', async () => {
    const lifecycle = deployLifecycleFixture();
    lifecycle.lifecycle!.nodes[3].items = [];
    await render({ status: 'ready', value: lifecycle });

    expect(byTestId('deploy-stage-production')?.textContent).toContain('No deploy steps for this environment');
  });
});
