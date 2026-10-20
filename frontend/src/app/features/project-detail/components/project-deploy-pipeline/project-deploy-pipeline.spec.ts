import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ProjectDeployPipelineComponent } from './project-deploy-pipeline';

describe('ProjectDeployPipelineComponent', () => {
  it('connects project.yml CI stages to approval and Production steps', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployPipelineComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployPipelineComponent);
    TestBed.runInInjectionContext(() => new ProjectDeployDataService()).fixture('demo')
      .subscribe(data => fixture.componentRef.setInput('snapshot', data));
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('.deploy-flow__stage')).toHaveLength(6);
    expect(host.querySelector('[data-testid="deploy-stage-build"]')?.textContent).toContain('commands.build');
    expect(host.querySelector('[data-testid="deploy-stage-production"]')?.textContent).toContain('Owner or Maintainer approval');
  });
});
