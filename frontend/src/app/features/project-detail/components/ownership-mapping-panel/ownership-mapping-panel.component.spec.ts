import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OwnershipMappingPanelComponent } from './ownership-mapping-panel.component';

describe('OwnershipMappingPanelComponent', () => {
  it('loads and saves a versioned Project Hub mapping', () => {
    TestBed.configureTestingModule({
      imports: [OwnershipMappingPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(OwnershipMappingPanelComponent);
    fixture.componentRef.setInput('projectName', 'Coding Agent Chat');
    fixture.detectChanges();
    http.expectOne('/api/v1/workspaces?includeArchived=true').flush([{ id: 'ws', projects: [{
      id: 'PROJ-003', displayName: 'Coding Agent Chat', shortCode: 'CAC', ownershipMappings: [mapping()],
    }] }]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="ownership-mapping-chat"]')).toBeTruthy();
    fixture.nativeElement.querySelector('[data-testid="save-ownership-mapping"]').click();
    const save = http.expectOne('/api/projects/PROJ-003/ownership-mappings/chat');
    expect(save.request.method).toBe('PUT');
    save.flush({ ...mapping(), version: 2, updatedBy: 'owner' });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Saved chat as version 2');
    http.verify();
  });
});

function mapping() {
  return {
    id: 'chat', observedSurfaces: ['Agent Studio chat'], component: 'Chat message',
    packageOrModule: 'coding-agent-chat', primaryProjectId: 'PROJ-003', repository: 'coding-agent-chat',
    consumerProjectIds: ['PROJ-002'], integrationHosts: ['Agent Studio'], releaseArtifact: 'npm package',
    versioningMechanism: 'npm', deploymentSteps: ['Publish'], environments: ['stable'],
    allowedTicketPrefix: 'CAC', evidence: ['contract'], confidence: 1, unresolvedAlternatives: [],
    version: 1, updatedAt: '2026-07-12T00:00:00Z', updatedBy: 'owner',
  };
}
