import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ProjectDeployReleasesComponent } from './project-deploy-releases';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

describe('ProjectDeployReleasesComponent', () => {
  it('selects a release and shows provenance and per-environment deployment', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployReleasesComponent], providers: [provideZonelessChangeDetection()] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployReleasesComponent);
    TestBed.runInInjectionContext(() => new ProjectDeployDataService()).fixture('demo')
      .subscribe(data => fixture.componentRef.setInput('snapshot', data));
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

  it('shows card-based notes from the selected release', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectDeployReleasesComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployReleasesComponent);
    TestBed.inject(ProjectDeployDataService).fixture('demo').subscribe(data => fixture.componentRef.setInput('snapshot', {
      ...data, source: 'api',
    }));
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/demo/deploy/releases/release-4/notes').flush({
      releaseId: 'release-4', version: '2026.10.16.4', preview: true,
      notes: [{ cardKey: 'WEB-36', text: 'Faster checkout' }],
    });
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="deploy-release-notes"]')?.textContent)
      .toContain('WEB-36 · Faster checkout');
    http.verify();
  });
});
