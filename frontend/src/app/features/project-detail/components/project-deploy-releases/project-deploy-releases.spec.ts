import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ProjectDeployReleasesComponent } from './project-deploy-releases';

describe('ProjectDeployReleasesComponent', () => {
  it('selects a release and shows provenance and per-environment deployment', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployReleasesComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployReleasesComponent);
    new ProjectDeployDataService().load('demo').subscribe(data => fixture.componentRef.setInput('snapshot', data));
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector<HTMLButtonElement>('[data-testid="deploy-release-release-2"]')!.click();
    fixture.detectChanges();
    const detail = host.querySelector('[data-testid="deploy-release-detail"]')!.textContent!;
    expect(detail).toContain('2026.10.15.2');
    expect(detail).toContain('sha256:9c8b7a6-demo-bundle');
    expect(detail).toContain('Approved by Owner');
    expect(detail).toContain('Production');
  });
});
