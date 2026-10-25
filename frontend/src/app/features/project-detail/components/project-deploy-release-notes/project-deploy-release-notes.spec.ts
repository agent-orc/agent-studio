import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { ProjectDeployDataService, type ReleaseNotes } from '../../services/project-deploy-data.service';
import type { DeployPanel } from '../../state/deploy-area.model';
import { deployReleasesFixture } from '../../testing/deploy-fixtures';
import { ProjectDeployReleaseNotesComponent } from './project-deploy-release-notes';

describe('ProjectDeployReleaseNotesComponent', () => {
  it('renders card titles and ignores notes from a previously selected release', async () => {
    const releases = deployReleasesFixture();
    const first = new Subject<DeployPanel<ReleaseNotes>>();
    const second = new Subject<DeployPanel<ReleaseNotes>>();
    const releaseNotes = (_project: string, id: string) => id === releases[0].id ? first : second;
    await TestBed.configureTestingModule({
      imports: [ProjectDeployReleaseNotesComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: ProjectDeployDataService, useValue: { releaseNotes } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectDeployReleaseNotesComponent);
    fixture.componentRef.setInput('release', releases[0]);
    fixture.detectChanges();
    await fixture.whenStable();
    first.next({ status: 'ready', value: { releaseId: releases[0].id, version: releases[0].version,
      preview: true, notes: [{ cardKey: 'WEB-35', text: 'Faster project search' }] } });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Faster project search');

    fixture.componentRef.setInput('release', releases[1]);
    fixture.detectChanges();
    await fixture.whenStable();
    first.next({ status: 'error', message: 'Stale release failed' });
    second.next({ status: 'ready', value: { releaseId: releases[1].id, version: releases[1].version,
      preview: false, notes: [{ cardKey: 'WEB-34', text: 'Clearer deployment status' }] } });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Production release');
    expect(fixture.nativeElement.textContent).toContain('Clearer deployment status');
    expect(fixture.nativeElement.textContent).not.toContain('Stale release failed');
  });
});
