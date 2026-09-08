import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { RetentionService } from '../../services/retention.service';
import { ArchivedTaskNoticeComponent } from './archived-task-notice.component';

const manifest = { taskId: 'task-1', taskKey: 'DEM-1', project: 'Demo', archivedAt: '2026-09-06T12:00:00Z', state: 'cold', totalBytes: 2048, restoredAt: null, tombstonedAt: null, stages: [] };

describe('ArchivedTaskNoticeComponent', () => {
  it('shows the cold summary and restores with progress', async () => {
    const retention = { getManifest: () => of(manifest), restoreTask: () => of({ restored: true, taskId: 'task-1' }) };
    await TestBed.configureTestingModule({ imports: [ArchivedTaskNoticeComponent], providers: [provideZonelessChangeDetection(), { provide: RetentionService, useValue: retention }] }).compileComponents();
    const fixture = TestBed.createComponent(ArchivedTaskNoticeComponent);
    fixture.componentRef.setInput('taskId', 'task-1'); fixture.componentRef.setInput('lane', '6-completed'); fixture.componentRef.setInput('archiveState', 'cold'); fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('2.0 KiB in cold storage');
    fixture.nativeElement.querySelector('[data-testid="archived-task-restore"]').click();
    await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="archived-task-notice"]')).toBeNull();
  });
});
