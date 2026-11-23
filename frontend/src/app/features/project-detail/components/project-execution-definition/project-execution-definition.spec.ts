import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ProjectExecutionDefinitionComponent } from './project-execution-definition';

describe('ProjectExecutionDefinitionComponent', () => {
  let fixture: ComponentFixture<ProjectExecutionDefinitionComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectExecutionDefinitionComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ProjectExecutionDefinitionComponent);
    fixture.componentRef.setInput('projectName', 'Quality Studio');
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('detects the stack and shows proposed verify commands on the setup view', () => {
    fixture.detectChanges();
    http.expectOne('/api/projects/Quality%20Studio/execution').flush({ valid: false, issues: [] });
    http.expectOne('/api/projects/Quality%20Studio/pipeline-definition').flush({ valid: true, issues: [] });
    http.expectNone('/api/projects/Quality%20Studio/execution/proposal-preview');
    fixture.componentRef.setInput('detectOnOpen', true);
    fixture.detectChanges();
    http.expectOne('/api/projects/Quality%20Studio/execution/proposal-preview').flush({
      detectedStacks: ['node'], reasoning: 'Found package.json', definition: '', prepareScript: '',
      reviewCommands: { build: ['npm run build'], test: ['npm test'], lint: ['npm run lint'] },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Build profile for node');
    expect(fixture.nativeElement.textContent).toContain('npm test');
  });

  it('renders repository truth and requires a justification before saving an override', async () => {
    fixture.detectChanges();
    http.expectOne('/api/projects/Quality%20Studio/execution').flush({
      repositoryDefinition: 'schemaVersion: 1',
      definitionSha256: '1234567890abcdef',
      valid: true,
      issues: [],
      preparationWarnings: [{
        code: 'cache-block-unused',
        block: 'nuget',
        consecutiveRuns: 3,
        message: 'The nuget block of Quality Studio is bound but unused for 3 consecutive preparations; the prepare script redirects or unsets NUGET_PACKAGES.',
      }],
      lastManifest: {
        completedAtUtc: '2026-09-12T08:00:00Z',
        durationMs: 1500,
        succeeded: true,
        caches: [{ block: 'npm', key: 'abcdef1234567890', state: 'hit' }],
      },
      override: null,
      source: 'subject-commit',
      branch: 'develop',
    });
    // The Pipeline section renders right after Execution and reads the same project.yml.
    http.expectOne('/api/projects/Quality%20Studio/pipeline-definition').flush({
      project: 'Quality Studio', path: '.agent-studio/project.yml', exists: true, schemaVersion: 1,
      valid: true, issues: [], definitionSha256: null, diagram: null, migrationAvailable: false,
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="execution-repository-definition"]')?.textContent)
      .toContain('schemaVersion: 1');
    expect(root.textContent).toContain('Source: develop tip');
    const cacheState = root.querySelector('[data-testid="execution-cache-state"]') as HTMLElement;
    expect(cacheState.textContent).toContain('hit');
    expect(cacheState.dataset['tone']).toBe('success');
    expect(root.querySelector('[data-testid="execution-preparation-warnings"]')?.textContent)
      .toContain('redirects or unsets NUGET_PACKAGES');
    expect((root.querySelector('[data-testid="execution-save-override"]') as HTMLButtonElement).disabled).toBe(true);

    fixture.componentInstance.justification.set('Temporary host compatibility while the repository change is reviewed.');
    fixture.detectChanges();
    expect((root.querySelector('[data-testid="execution-save-override"]') as HTMLButtonElement).disabled).toBe(false);
  });
});
