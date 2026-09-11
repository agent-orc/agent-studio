import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { RetentionService } from '../../services/retention.service';
import { ArchivedFilesManifestComponent } from './archived-files-manifest.component';

describe('ArchivedFilesManifestComponent', () => {
  it('lists archived names, sizes, hashes, and the hot-excerpt explanation', async () => {
    const retention = { restoredTaskId: signal<string | null>(null), getManifest: () => of({ taskId: '1', taskKey: 'DEM-1', project: 'Demo', archivedAt: '', state: 'cold', totalBytes: 42, restoredAt: null, tombstonedAt: null, stages: [{ stage: 1, archivedAt: '', payloadPath: '', payloadSha256: '', totalBytes: 42, files: [{ name: 'cli-output.log', size: 42, sha256: 'abc123' }], policyVersion: 1, archivedBy: 'operator' }] }) };
    await TestBed.configureTestingModule({ imports: [ArchivedFilesManifestComponent], providers: [provideZonelessChangeDetection(), { provide: RetentionService, useValue: retention }] }).compileComponents();
    const fixture = TestBed.createComponent(ArchivedFilesManifestComponent);
    fixture.componentRef.setInput('jobId', '1'); fixture.componentRef.setInput('archived', true); fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('cli-output.log');
    expect(fixture.nativeElement.textContent).toContain('abc123');
    expect(fixture.nativeElement.textContent).toContain('Hot Markdown excerpts appear below');
  });
});
