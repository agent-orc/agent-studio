import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import type { TaskPreview } from '../../models/task.model';
import { TaskPreviewComponent } from './task-preview.component';

const preview: TaskPreview = {
  address: 'https://agt-3180.demo.preview.internal', status: 'ready',
  deliverySha: 'a'.repeat(40), screenshotUrl: '/api/projects/demo/previews/agt-3180/screenshot',
  updatedAtUtc: '2026-10-20T12:00:00Z', expiresAtUtc: '2026-10-27T12:00:00Z', jobId: 'job-1',
};

describe('TaskPreviewComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskPreviewComponent], providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('shows the ready delivery and screenshot on the detail view', () => {
    const fixture = TestBed.createComponent(TaskPreviewComponent);
    fixture.componentRef.setInput('preview', preview);
    fixture.componentRef.setInput('variant', 'detail');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="task-detail-preview"]')?.textContent).toContain('aaaaaaaaaaaa');
    expect(root.querySelector('a')?.href).toBe(preview.address + '/');
    expect(root.querySelector('img')?.getAttribute('src')).toBe(preview.screenshotUrl);
  });

  it('does not link a deployment until it is ready', () => {
    const fixture = TestBed.createComponent(TaskPreviewComponent);
    fixture.componentRef.setInput('preview', { ...preview, status: 'deploying' });
    fixture.componentRef.setInput('variant', 'chat');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="activity-chat-preview"]')?.textContent).toContain('deploying');
    expect(root.querySelector('a')).toBeNull();
  });

  it('keeps the board card open when its preview link is activated', () => {
    const fixture = TestBed.createComponent(TaskPreviewComponent);
    fixture.componentRef.setInput('preview', preview);
    fixture.componentRef.setInput('taskKey', 'AGT-3180');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const parentClick = vi.fn();
    root.addEventListener('click', parentClick);
    root.querySelector('a')!.click();
    expect(parentClick).not.toHaveBeenCalled();
    expect(root.querySelector('a')?.getAttribute('aria-label')).toBe('Open preview for AGT-3180');
  });
});
