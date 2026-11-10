import { TestBed } from '@angular/core/testing';
import { ProjectBuildProfileNoticeComponent } from './project-build-profile-notice.component';

describe('ProjectBuildProfileNoticeComponent', () => {
  it.each([
    [null, 'No BuildProfile is declared and no verify commands can be derived.'],
    [{ status: 'declared' }, 'The declared BuildProfile and repository layout provide no verify commands.'],
  ])('explains the empty verify plan for profile %j', async (profile, expectedCopy) => {
    await TestBed.configureTestingModule({ imports: [ProjectBuildProfileNoticeComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBuildProfileNoticeComponent);
    fixture.componentRef.setInput('summary', {
      profile,
      gateApplicable: false,
      verifyPlan: { source: 'none', commands: [] },
    });
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="project-settings-no-verify-commands"]',
    ) as HTMLElement;
    expect(notice.textContent).toContain(expectedCopy);
    expect(notice.textContent).toContain('Not applicable');
    expect(notice.querySelector('a')?.href).toContain('contributor-setup.md#onboarding-checklist');
  });

  it('names the missing project definition and shows its example', async () => {
    await TestBed.configureTestingModule({ imports: [ProjectBuildProfileNoticeComponent] }).compileComponents();
    const fixture = TestBed.createComponent(ProjectBuildProfileNoticeComponent);
    fixture.componentRef.setInput('summary', {
      profile: null,
      gateApplicable: false,
      verifyPlan: { source: 'none', commands: [] },
      verifyDefinition: {
        state: 'missing',
        path: '.agent-studio/project.yml',
        message: 'Verify definition missing: ...',
        exampleStack: 'go',
        exampleDefinition: 'schemaVersion: 1\nstack: [go]\n',
        examplePrepareScript: '#!/bin/sh\n',
      },
    });
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="project-settings-no-verify-commands"]',
    ) as HTMLElement;
    expect(notice.textContent).toContain('.agent-studio/project.yml');
    expect(notice.textContent).toContain('Merges refused');
    expect(notice.textContent).toContain('go repository');
    expect(notice.querySelector('[data-testid="project-settings-verify-example"]')?.textContent)
      .toContain('stack: [go]');
    expect(notice.querySelector('a')?.href).toContain('onboard-a-project.md#test-command-output-in-review');
  });
});
