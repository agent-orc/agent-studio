import { TestBed } from '@angular/core/testing';
import { RoutingPreviewComponent } from './routing-preview.component';
import type { ComponentRoutingResolution } from '../../../../models/task.model';

describe('RoutingPreviewComponent', () => {
  it('shows the cross-project routing decision', () => {
    const fixture = TestBed.configureTestingModule({ imports: [RoutingPreviewComponent] })
      .createComponent(RoutingPreviewComponent);
    fixture.componentRef.setInput('routing', route(false));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="routing-preview"]').textContent)
      .toContain('Create CAC ticket; integrate/deploy in Agent Studio.');
    expect(fixture.nativeElement.textContent).toContain('owned by Coding Agent Chat');
  });

  it('blocks silent routing when ownership needs a question', () => {
    const fixture = TestBed.configureTestingModule({ imports: [RoutingPreviewComponent] })
      .createComponent(RoutingPreviewComponent);
    fixture.componentRef.setInput('routing', route(true));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Mappings conflict');
    const status = fixture.nativeElement.querySelector('[data-testid="routing-preview-status"]') as HTMLElement;
    expect(status.getAttribute('data-tone')).toBe('warning');
    expect(status.textContent?.trim()).toBe('Mappings conflict');
  });

  it('shows a running indicator while ownership resolves', () => {
    const fixture = TestBed.configureTestingModule({ imports: [RoutingPreviewComponent] })
      .createComponent(RoutingPreviewComponent);
    fixture.componentRef.setInput('pending', true);
    fixture.detectChanges();
    const status = fixture.nativeElement.querySelector('[data-testid="routing-preview-status"]') as HTMLElement;
    expect(status.getAttribute('data-tone')).toBe('running');
    expect(status.textContent?.trim()).toBe('Resolving component ownership…');
  });
});

function route(requiresQuestion: boolean): ComponentRoutingResolution {
  return {
    observedSurface: 'Agent Studio chat', component: 'message', packageOrModule: 'coding-agent-chat',
    navigationProject: { id: 'PROJ-002', shortCode: 'AGT', displayName: 'Agent Studio' },
    primaryProject: requiresQuestion ? null : { id: 'PROJ-003', shortCode: 'CAC', displayName: 'Coding Agent Chat' },
    repository: 'coding-agent-chat', consumerProjects: [], integrationHosts: ['Agent Studio'],
    releaseArtifact: 'npm package', versioningMechanism: 'npm', deploymentSteps: [], environments: [],
    allowedTicketPrefix: requiresQuestion ? null : 'CAC', storageProjectId: requiresQuestion ? null : 'PROJ-003',
    evidence: [], confidence: requiresQuestion ? 0.4 : 1, unresolvedAlternatives: [], requiresQuestion,
    questionReason: requiresQuestion ? 'Mappings conflict' : null,
    preview: 'Create CAC ticket; integrate/deploy in Agent Studio.', mappingId: 'chat', mappingVersion: 2,
  };
}
