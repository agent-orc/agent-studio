import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { StudioShellComponent } from './studio-shell.component';
import { StudioTabStateService } from './services/studio-tab-state.service';

describe('repository URL navigation', () => {
  it('opens the well-known repository entry in Git View', async () => {
    await TestBed.configureTestingModule({
      imports: [StudioShellComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(),
        provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(StudioShellComponent);
    const tabs = TestBed.inject(StudioTabStateService);
    fixture.componentInstance.openUrlPreview({ projectName: 'Alpha', urlId: 'repo' });
    expect(tabs.activeTab()).toEqual({ kind: 'hub', projectName: 'Alpha', section: 'git' });
  });
});
