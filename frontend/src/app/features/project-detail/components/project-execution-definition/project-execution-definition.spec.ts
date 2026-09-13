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

  it('renders repository truth and requires a justification before saving an override', async () => {
    fixture.detectChanges();
    http.expectOne('/api/projects/Quality%20Studio/execution').flush({
      repositoryDefinition: 'schemaVersion: 1',
      definitionSha256: '1234567890abcdef',
      valid: true,
      issues: [],
      lastManifest: {
        completedAtUtc: '2026-09-12T08:00:00Z',
        durationMs: 1500,
        succeeded: true,
        caches: [{ block: 'npm', key: 'abcdef1234567890', state: 'hit' }],
      },
      override: null,
      source: 'subject-commit',
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="execution-repository-definition"]')?.textContent)
      .toContain('schemaVersion: 1');
    expect(root.querySelector('.execution__cache-state')?.textContent).toContain('hit');
    expect((root.querySelector('[data-testid="execution-save-override"]') as HTMLButtonElement).disabled).toBe(true);

    fixture.componentInstance.justification.set('Temporary host compatibility while the repository change is reviewed.');
    fixture.detectChanges();
    expect((root.querySelector('[data-testid="execution-save-override"]') as HTMLButtonElement).disabled).toBe(false);
  });
});
