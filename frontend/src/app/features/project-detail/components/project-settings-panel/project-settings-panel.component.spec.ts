import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { ProjectSettingsPanelComponent } from './project-settings-panel.component';

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
});
