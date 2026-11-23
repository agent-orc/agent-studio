import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { ProjectSetupHeaderComponent } from './project-setup-header';

describe('ProjectSetupHeaderComponent', () => {
  it('maps setup sections and emits review and release navigation', () => {
    TestBed.configureTestingModule({ imports: [ProjectSetupHeaderComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(ProjectSetupHeaderComponent);
    const review = vi.fn();
    const release = vi.fn();
    fixture.componentInstance.reviewAspects.subscribe(review);
    fixture.componentInstance.release.subscribe(release);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="project-settings-jump-checkout"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="project-settings-jump-verify"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="project-settings-jump-execution"]')).not.toBeNull();
    (root.querySelector('[data-testid="project-settings-review-aspects"]') as HTMLButtonElement).click();
    (root.querySelector('[data-testid="project-settings-release"]') as HTMLButtonElement).click();
    expect(review).toHaveBeenCalledOnce();
    expect(release).toHaveBeenCalledOnce();
  });
});
