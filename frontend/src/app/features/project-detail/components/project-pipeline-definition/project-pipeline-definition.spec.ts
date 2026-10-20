import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type {
  PipelineDefinitionResponse,
  PipelineMigrationResponse,
} from '../../services/project-pipeline-definition.service';
import { ProjectPipelineDefinitionComponent } from './project-pipeline-definition';

const URL = '/api/projects/Agent%20Studio%20Website/pipeline-definition';

const SCHEMA_ONE: PipelineDefinitionResponse = {
  project: 'Agent Studio Website',
  path: '.agent-studio/project.yml',
  exists: true,
  schemaVersion: 1,
  valid: true,
  issues: [],
  definitionSha256: 'a'.repeat(64),
  diagram: {
    schemaVersion: 1,
    gate: { source: 'commands', build: ['npm run build'], test: ['npm test'], testSuites: [] },
    artifacts: [],
    environments: [],
    environmentsDefaulted: false,
    lifecycleDefaulted: false,
  },
  migrationAvailable: true,
};

const PROPOSAL: PipelineMigrationResponse = {
  project: 'Agent Studio Website',
  available: true,
  baseSha256: 'a'.repeat(64),
  proposedYaml: 'schemaVersion: 2\nenvironments:\n  - name: staging\n',
  proposalSha256: 'b'.repeat(64),
  valid: true,
  issues: [],
  notes: ['No built bundle directory was found.'],
  diagram: {
    ...SCHEMA_ONE.diagram!,
    schemaVersion: 2,
    environments: [
      { path: 'environments[0]', name: 'staging', order: 0, protected: false, automatic: true, approval: [], steps: [], variables: [] },
      { path: 'environments[1]', name: 'production', order: 1, protected: true, automatic: false, approval: ['Owner'], steps: [], variables: [] },
    ],
  },
};

describe('ProjectPipelineDefinitionComponent', () => {
  let fixture: ComponentFixture<ProjectPipelineDefinitionComponent>;
  let http: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPipelineDefinitionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectPipelineDefinitionComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio Website');
    http = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => http.verify());

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('shows validation issues with line and path next to the diagram', async () => {
    fixture.detectChanges();
    http.expectOne(URL).flush({
      ...SCHEMA_ONE,
      valid: false,
      migrationAvailable: false,
      issues: [{ path: 'commands.prepare', code: 'prepare-script-missing', message: 'The prepare script does not exist.', line: 5 }],
    });
    await settle();

    expect(root.querySelector('[data-testid="pipeline-validation-status"]')?.textContent).toContain('1 issue');
    const issue = root.querySelector('[data-testid="pipeline-validation-issues"] li')!;
    expect(issue.textContent).toContain('Line 5');
    expect(issue.textContent).toContain('commands.prepare');
    expect(root.querySelector('[data-testid="pipeline-diagram"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="pipeline-migration-preview"]')).toBeNull();
  });

  it('previews the migration as a dry run and writes only on confirmation', async () => {
    fixture.detectChanges();
    http.expectOne(URL).flush(SCHEMA_ONE);
    await settle();
    expect(root.querySelector('[data-testid="pipeline-schema-version"]')?.textContent).toContain('Schema 1');

    (root.querySelector('[data-testid="pipeline-migration-preview"]') as HTMLButtonElement).click();
    http.expectOne(`${URL}/migration`).flush(PROPOSAL);
    await settle();

    expect(root.querySelector('[data-testid="pipeline-migration-yaml"]')?.textContent).toContain('schemaVersion: 2');
    expect(root.querySelector('[data-testid="pipeline-migration-notes"]')?.textContent).toContain('No built bundle');
    expect(root.querySelector('[data-testid="pipeline-migration"] [data-testid="pipeline-node-production"]')).not.toBeNull();
    http.expectNone(request => request.method === 'POST');

    (root.querySelector('[data-testid="pipeline-migration-confirm"]') as HTMLButtonElement).click();
    const confirm = http.expectOne(request => request.method === 'POST' && request.url === `${URL}/migration`);
    expect(confirm.request.body).toEqual({ baseSha256: 'a'.repeat(64), proposalSha256: 'b'.repeat(64) });
    confirm.flush({
      changed: true,
      commitSha: '0123456789abcdef',
      pushQueued: true,
      definition: { ...SCHEMA_ONE, schemaVersion: 2, migrationAvailable: false, diagram: PROPOSAL.diagram },
    });
    await settle();

    expect(root.querySelector('[role="status"]')?.textContent).toContain('commit 0123456');
    expect(root.querySelector('[data-testid="pipeline-migration"]')).toBeNull();
    expect(root.querySelector('[data-testid="pipeline-schema-version"]')?.textContent).toContain('Schema 2');
  });

  it('keeps an invalid proposal from being confirmed and reports a stale dry run', async () => {
    fixture.detectChanges();
    http.expectOne(URL).flush(SCHEMA_ONE);
    await settle();
    (root.querySelector('[data-testid="pipeline-migration-preview"]') as HTMLButtonElement).click();
    http.expectOne(`${URL}/migration`).flush({
      ...PROPOSAL,
      valid: false,
      issues: [{ path: 'deploy[0].directory', code: 'deploy-directory-invalid', message: 'Absolute directory needed.', line: 31 }],
    });
    await settle();

    expect((root.querySelector('[data-testid="pipeline-migration-confirm"]') as HTMLButtonElement).disabled).toBe(true);
    expect(root.querySelector('[data-testid="pipeline-migration-issues"]')?.textContent).toContain('Line 31');

    fixture.componentInstance.migration.set(PROPOSAL);
    fixture.detectChanges();
    (root.querySelector('[data-testid="pipeline-migration-confirm"]') as HTMLButtonElement).click();
    http.expectOne(`${URL}/migration`).flush(
      { error: 'project.yml or the environment declarations changed since the dry run.', code: 'stale' },
      { status: 409, statusText: 'Conflict' });
    await settle();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('changed since the dry run');
  });
});
