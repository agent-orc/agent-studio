import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { ExplorerProjectDestinationsComponent } from './explorer-project-destinations';

describe('ExplorerProjectDestinationsComponent', () => {
  it('highlights Settings and emits its destination for the containing project', () => {
    TestBed.configureTestingModule({ imports: [ExplorerProjectDestinationsComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
    ] });
    const fixture = TestBed.createComponent(ExplorerProjectDestinationsComponent);
    fixture.componentRef.setInput('project', { name: 'New project', displayLabel: 'New project',
      initial: 'N', color: '', totalJobs: 0, isActive: true, projectId: null,
      workspaceId: null, shortCode: null, urls: [] });
    fixture.componentRef.setInput('activeSurface', 'settings');
    fixture.componentRef.setInput('metricView', 'numbers');
    const navigate = vi.fn();
    fixture.componentInstance.navigate.subscribe(navigate);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const settings = root.querySelector('[data-testid="studio-explorer-project-settings-New project"]') as HTMLElement;
    expect(settings.getAttribute('aria-current')).toBe('page');
    settings.click();
    expect(navigate).toHaveBeenCalledWith('settings');
    expect(root.querySelector('[data-testid="studio-explorer-project-hub-New project"]')?.getAttribute('aria-current')).toBeNull();
  });
});
