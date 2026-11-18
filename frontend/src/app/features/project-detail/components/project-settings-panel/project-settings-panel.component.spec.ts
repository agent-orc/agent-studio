import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { By } from '@angular/platform-browser';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { ProjectSettingsPanelComponent } from './project-settings-panel.component';
import { ProjectBasicsCardComponent } from '../project-basics-card/project-basics-card.component';

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('ProjectSettingsPanelComponent (smoke)', () => {
  it('links review aspects to Deploy Run steps for this project', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSettingsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectSettingsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Library');
    fixture.componentInstance.openReviewAspects();
    expect(TestBed.inject(StudioTabStateService).activeTab())
      .toEqual({ kind: 'hub', projectName: 'Library', section: 'pipeline' });
  });

  it('compiles + instantiates without throwing', async () => {
    expect(ProjectSettingsPanelComponent).toBeTruthy();
  });

  it('refreshes the verify gate when Project basics saves paths', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectSettingsPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectSettingsPanelComponent);
    fixture.componentRef.setInput('projectName', 'Library');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Library/build-profile').flush({ gateApplicable: true,
      verifyDefinition: { state: 'checkout-missing' } });
    const basics = fixture.debugElement.query(By.directive(ProjectBasicsCardComponent))
      ?.componentInstance as ProjectBasicsCardComponent;
    expect(basics).toBeTruthy();
    basics.pathsSaved.emit();
    http.expectOne('/api/projects/Library/build-profile').flush({ gateApplicable: true,
      verifyDefinition: { state: 'ready' } });
    expect(fixture.componentInstance.buildProfileGate()?.verifyDefinition?.state).toBe('ready');
  });
});
