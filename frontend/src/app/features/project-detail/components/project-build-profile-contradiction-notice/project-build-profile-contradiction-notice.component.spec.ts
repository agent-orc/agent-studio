import { TestBed } from '@angular/core/testing';
import { ProjectBuildProfileContradictionNoticeComponent } from './project-build-profile-contradiction-notice.component';

describe('ProjectBuildProfileContradictionNoticeComponent', () => {
  it('warns that project.yml overrides the declared BuildProfile command', async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectBuildProfileContradictionNoticeComponent],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBuildProfileContradictionNoticeComponent);
    fixture.componentRef.setInput('contradictions', [
      {
        field: 'test',
        profileCommands: ['dotnet test QualityStudio.slnx --filter "Category!=MachineBound"'],
        repositoryCommands: [
          'dotnet test QualityStudio.slnx --filter "Category!=MachineBound&Category!=ExternalLive"',
        ],
      },
    ]);
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="project-settings-build-profile-contradiction"]',
    ) as HTMLElement;
    expect(notice.textContent).toContain('Build profile disagrees with project.yml');
    expect(notice.textContent).toContain('test:');
    expect(notice.textContent).toContain('Category!=MachineBound&Category!=ExternalLive');
  });
});
