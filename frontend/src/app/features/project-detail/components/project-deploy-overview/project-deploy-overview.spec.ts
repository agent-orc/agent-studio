import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ProjectDeployOverviewComponent } from './project-deploy-overview';

describe('ProjectDeployOverviewComponent', () => {
  it('shows one status per environment and keeps fixture actions inert', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployOverviewComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployOverviewComponent);
    new ProjectDeployDataService().load('demo').subscribe(data => fixture.componentRef.setInput('snapshot', data));
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-testid^="deploy-environment-"]')).toHaveLength(2);
    expect(host.querySelector('[data-testid="deploy-environment-production"]')?.textContent).toContain('WEB-33');
    expect(host.querySelector('[data-testid="deploy-waiting-releases"]')?.textContent).toContain('2026.10.16.4');
    expect(host.querySelectorAll('.deploy-actions button:disabled')).toHaveLength(6);
  });
});
