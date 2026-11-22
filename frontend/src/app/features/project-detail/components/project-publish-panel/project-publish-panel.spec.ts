import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { TaskService } from '../../../../services/task.service';
import { ProjectPublishPanelComponent } from './project-publish-panel';

describe('ProjectPublishPanelComponent release commands', () => {
  it('explains missing release definitions and links the setup guide', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectPublishPanelComponent],
      providers: [provideZonelessChangeDetection(), { provide: TaskService, useValue: {} }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectPublishPanelComponent);
    fixture.componentRef.setInput('projectName', 'library');
    fixture.componentRef.setInput('targets', []);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No package release definition found');
    expect(fixture.nativeElement.querySelector('a').href).toContain('library-releases.md');
  });

  it('previews a first release without triggering a real release', async () => {
    const target = { id: 'package:npm', kind: 'package', label: 'npm', firstPublishPending: true,
      releaseCommand: 'bash scripts/release.sh', releaseBranch: 'main' };
    const calls: unknown[][] = [];
    const preview = new Subject<{ status: string; workflow: string; version: string }>();
    await TestBed.configureTestingModule({
      imports: [ProjectPublishPanelComponent],
      providers: [provideZonelessChangeDetection(), { provide: TaskService, useValue: {
        getPublishPanel: () => of({ target, suggestedVersion: '0.1.0', pendingTasks: [], automationMode: 'manual' }),
        publishPackage: (...args: unknown[]) => {
          calls.push(args);
          return preview;
        },
      } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectPublishPanelComponent);
    fixture.componentRef.setInput('projectName', 'library');
    fixture.componentRef.setInput('targets', [target]);
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="publish-badge-package:npm"]').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Release branch: main');
    fixture.nativeElement.querySelector('[data-testid="publish-dry-run"]').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="publish-dry-run"]').getAttribute('aria-busy')).toBe('true');
    preview.next({ status: 'preview', workflow: 'Branch: main', version: '0.1.0' });
    preview.complete();
    fixture.detectChanges();
    expect(calls).toEqual([['library', 'package:npm', '0.1.0', true]]);
    expect(fixture.nativeElement.textContent).toContain('Branch: main');
  });
});
