import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { PipelineDefinitionIssue, PipelineDiagram } from '../../services/project-pipeline-definition.service';
import { ProjectPipelineDiagramComponent } from './project-pipeline-diagram';

const SCHEMA_TWO: PipelineDiagram = {
  schemaVersion: 2,
  gate: { source: 'commands', build: ['npm run build'], test: ['npm test'], testSuites: ['unit'] },
  artifacts: [{ path: 'artifacts[0]', name: 'site', type: 'static', source: 'dist', image: null }],
  environments: [
    {
      path: 'environments[0]', name: 'staging', order: 0, protected: false, automatic: true, approval: [], requires: null,
      steps: [{
        path: 'deploy[0]', kind: 'static-site', artifact: 'site', target: 'web-vm',
        destination: '/srv/www/_staging/studio', healthUrl: 'https://example.test/_staging/', healthTimeoutSeconds: 60,
      }],
      variables: [{ path: 'variables[0]', name: 'LOG_LEVEL', isSecret: false, secret: null }],
    },
    {
      path: 'environments[1]', name: 'production', order: 1, protected: true, automatic: false,
      approval: ['Owner', 'Maintainer'], requires: 'staging',
      steps: [{
        path: 'deploy[1]', kind: 'container', artifact: 'site', target: 'run-host',
        destination: '/srv/apps/site/compose.yml (site)', healthUrl: 'https://example.test/healthz', healthTimeoutSeconds: 120,
      }],
      variables: [{ path: 'variables[1]', name: 'DATABASE_URL', isSecret: true, secret: 'DATABASE_URL' }],
    },
  ],
  environmentsDefaulted: false,
  lifecycleDefaulted: false,
};

describe('ProjectPipelineDiagramComponent', () => {
  let fixture: ComponentFixture<ProjectPipelineDiagramComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelineDiagramComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectPipelineDiagramComponent);
  });

  function render(diagram: PipelineDiagram, issues: PipelineDefinitionIssue[] = []): HTMLElement {
    fixture.componentRef.setInput('diagram', diagram);
    fixture.componentRef.setInput('issues', issues);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('draws gate, artifact, Staging and Production in lifecycle order', () => {
    const root = render(SCHEMA_TWO);

    const nodes = Array.from(root.querySelectorAll('[data-testid="pipeline-diagram"] > li'))
      .map(node => node.getAttribute('data-testid'));
    expect(nodes).toEqual(['pipeline-node-gate', 'pipeline-node-artifacts', 'pipeline-node-staging', 'pipeline-node-production']);
    expect(root.querySelector('[data-testid="pipeline-node-gate"]')?.textContent).toContain('npm run build');
    expect(root.querySelector('[data-testid="pipeline-artifact-site"]')?.textContent).toContain('Static bundle');
    expect(root.querySelector('[data-testid="pipeline-node-staging"]')?.textContent).toContain('Automatic');
  });

  it('shows protection, approval, SSH steps with health checks and secret names', () => {
    const root = render(SCHEMA_TWO);

    const production = root.querySelector('[data-testid="pipeline-node-production"]')!;
    expect(root.querySelector('[data-testid="pipeline-protected-production"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="pipeline-protected-staging"]')).toBeNull();
    expect(production.textContent).toContain('Approval: Owner or Maintainer');
    expect(production.textContent).toContain('After Staging');
    const step = root.querySelector('[data-testid="pipeline-step-production-container"]')!;
    expect(step.textContent).toContain('Compose service over SSH');
    expect(step.textContent).toContain('https://example.test/healthz');
    expect(step.textContent).toContain('within 120 s');
    expect(production.textContent).toContain('secret DATABASE_URL');
    expect(root.querySelector('[data-testid="pipeline-step-staging-static-site"]')?.textContent)
      .toContain('/srv/www/_staging/studio');
  });

  it('marks the node that an issue path points into', () => {
    const root = render(SCHEMA_TWO, [
      { path: 'deploy[1].healthTimeoutSeconds', code: 'deploy-health-timeout-invalid', message: 'bad', line: 40 },
      { path: 'artifacts[0].path', code: 'artifact-path-invalid', message: 'bad', line: 12 },
    ]);

    expect(root.querySelector('[data-testid="pipeline-node-production"]')?.getAttribute('data-issues')).toBe('true');
    expect(root.querySelector('[data-testid="pipeline-node-production"]')?.textContent).toContain('1 issue');
    expect(root.querySelector('[data-testid="pipeline-node-staging"]')?.getAttribute('data-issues')).toBe('false');
    expect(root.querySelector('[data-testid="pipeline-node-artifacts"]')?.getAttribute('data-issues')).toBe('true');
  });

  it('explains that schema 1 has no environments yet', () => {
    const root = render({ ...SCHEMA_TWO, schemaVersion: 1, artifacts: [], environments: [] });

    expect(root.querySelector('[data-testid="pipeline-node-no-environments"]')?.textContent)
      .toContain('Migrate to schema 2');
    expect(root.querySelector('[data-testid="pipeline-node-artifacts"]')?.textContent).toContain('No artifact declared');
  });
});
