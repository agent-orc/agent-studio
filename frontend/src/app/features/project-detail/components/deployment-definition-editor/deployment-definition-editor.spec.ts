import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DeploymentDefinitionEditorComponent } from './deployment-definition-editor';

describe('DeploymentDefinitionEditorComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [DeploymentDefinitionEditorComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(DeploymentDefinitionEditorComponent);
    fixture.componentRef.setInput('projectName', 'Demo Project');
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController) };
  }

  it('builds a labelled preview from a valid repository command', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.command.set('bash scripts/deploy.sh --branch {{branch}}');
    fixture.componentInstance.addParameter();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="deployment-preview-form"]').click();

    const request = http.expectOne('/api/projects/Demo%20Project/deployment/compile');
    expect(request.request.body.prompt).toContain('Parameter: branch branch');
    expect(request.request.body.prompt).toContain('# Label: Branch to deploy');
    request.flush({
      title: 'Deployment for Demo Project', summary: 'Valid',
      command: 'bash scripts/deploy.sh --branch {{branch}}', warnings: [], runnable: true,
      parameters: [
        { name: 'branch', type: 'branch', required: true, default: null, options: [] },
        { name: 'confirm', type: 'boolean', required: true, default: false, options: [] },
      ],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="deployment-definition-result"]').textContent).toContain('Definition valid');
    expect(fixture.nativeElement.querySelector('[data-testid="deployment-definition-status"]').dataset['tone']).toBe('success');
    expect(fixture.nativeElement.querySelector('[data-testid="deployment-form-preview"]').textContent).toContain('Branch to deploy *');
    expect(fixture.nativeElement.querySelector('[data-testid="deployment-form-preview"]').textContent).toContain('Confirm deployment *');
  });

  it('announces loading while the definition is being validated', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.command.set('bash scripts/deploy.sh');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="deployment-preview-form"]').click();
    fixture.detectChanges();

    const result = fixture.nativeElement.querySelector('[data-testid="deployment-definition-result"]');
    expect(result.textContent).toContain('Validating definition');
    expect(result.getAttribute('aria-busy')).toBe('true');
    expect(result.querySelector('[data-testid="deployment-definition-status"]').dataset['tone']).toBe('running');
    http.expectOne('/api/projects/Demo%20Project/deployment/compile').flush({
      title: 'Deployment', summary: 'Valid', command: 'bash scripts/deploy.sh',
      parameters: [{ name: 'confirm', type: 'boolean', required: true, default: false, options: [] }],
      warnings: [], runnable: true,
    });
  });

  it('associates parameter validation with the relevant labelled field', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.command.set('bash scripts/deploy.sh --branch {{branch}}');
    fixture.componentInstance.addParameter();
    fixture.componentInstance.updateParameter(1, { label: '' });
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="deployment-preview-form"]').click();
    fixture.detectChanges();

    const label = fixture.nativeElement.querySelector('[data-testid="deployment-parameter-label-0"]');
    expect(label.getAttribute('aria-invalid')).toBe('true');
    expect(label.getAttribute('aria-describedby')).toContain('deployment-parameter-label-errors-1');
    expect(fixture.nativeElement.querySelector('#deployment-parameter-label-errors-1').textContent)
      .toContain('Add the label operators will see.');
    expect(fixture.nativeElement.querySelector('[data-testid="deployment-definition-result"]').textContent)
      .toContain('Definition needs attention');
    http.expectNone('/api/projects/Demo%20Project/deployment/compile');
  });

  it('associates an invalid command warning with the command field', async () => {
    const { fixture, http } = await setup();
    fixture.componentInstance.command.set('npm run deploy');
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="deployment-preview-form"]').click();
    http.expectOne('/api/projects/Demo%20Project/deployment/compile').flush({
      title: 'Deployment', summary: 'Invalid', command: null, parameters: [], runnable: false,
      warnings: ['The command must be a repository-owned scripts/*.sh path with typed slots and no shell chaining or redirection.'],
    });
    fixture.detectChanges();

    const command = fixture.nativeElement.querySelector('[data-testid="deployment-command"]');
    expect(command.getAttribute('aria-invalid')).toBe('true');
    expect(command.getAttribute('aria-describedby')).toContain('deployment-command-error');
    expect(fixture.nativeElement.querySelector('[data-testid="deployment-command-error"]').textContent).toContain('repository-owned');
  });
});
