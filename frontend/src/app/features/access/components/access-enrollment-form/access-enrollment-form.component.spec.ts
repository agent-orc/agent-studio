import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { runHostCommand } from '../../models/access.model';
import { AccessService } from '../../services/access.service';
import { ENROLLMENT_CODE, fakeAccessService } from '../../testing/access-fixtures';
import { AccessEnrollmentFormComponent } from './access-enrollment-form.component';

describe('AccessEnrollmentFormComponent', () => {
  let access: ReturnType<typeof fakeAccessService>;

  beforeEach(async () => {
    access = fakeAccessService();
    await TestBed.configureTestingModule({
      imports: [AccessEnrollmentFormComponent],
      providers: [provideZonelessChangeDetection(), { provide: AccessService, useValue: access }],
    }).compileComponents();
  });

  function mount() {
    const fixture = TestBed.createComponent(AccessEnrollmentFormComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const name = (value: string) => {
      const input = element.querySelector('[data-testid="access-enrollment-name"]') as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    const kind = (value: string) => {
      const select = element.querySelector('[data-testid="access-enrollment-kind"]') as HTMLSelectElement;
      select.value = value;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    const create = async () => {
      fixture.detectChanges();
      (element.querySelector('[data-testid="access-enrollment-create"]') as HTMLButtonElement).click();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { fixture, element, name, kind, create };
  }

  it('creates an engine enrollment code and shows it once, with a copy control', async () => {
    const { fixture, element, name, kind, create } = mount();
    name('engine-host');
    kind('orchestrator');
    expect(element.querySelector('[data-testid="access-enrollment-projects"]')).toBeNull();
    await create();

    expect(access.createEnrollment).toHaveBeenCalledWith('engine-host', 'orchestrator');
    const issued = element.querySelector('[data-testid="access-enrollment-code"]')!;
    expect(issued.textContent).toContain('Shown once');
    expect(issued.textContent).toContain('expires 30 days after enrollment');
    expect(element.querySelector('[data-testid="access-enrollment-code-value"]')!.textContent).toBe(ENROLLMENT_CODE);
    expect(element.querySelector('[data-testid="access-run-host-command"]')).toBeNull();

    (element.querySelector('[data-testid="access-enrollment-code-done"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.textContent).not.toContain(ENROLLMENT_CODE);
  });

  it('binds a runner enrollment code to the chosen projects and warns that none binds nothing', async () => {
    const { fixture, element, name, kind, create } = mount();
    kind('coding');
    expect(element.querySelector('[data-testid="access-enrollment-no-project"]')?.textContent).toContain('claims and reads nothing');
    name('build-host');
    (element.querySelector('[data-testid="access-enrollment-project-PROJ-001"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="access-enrollment-no-project"]')).toBeNull();
    await create();

    expect(access.createEnrollment).toHaveBeenCalledWith('build-host', 'coding', ['PROJ-001']);
  });

  it('chooses all projects in place of single projects', () => {
    const { fixture } = mount();
    const component = fixture.componentInstance;
    component.toggle('PROJ-001');
    component.toggle(component.allProjects);
    expect(component.selected()).toEqual(['*']);
    component.toggle('PROJ-002');
    expect(component.selected()).toEqual(['PROJ-002']);
  });

  it('shows the host commands with a run host code (F4)', async () => {
    const { fixture, element, name, kind, create } = mount();
    name('stage-host');
    kind('deploy');
    expect(element.querySelector('[data-testid="access-enrollment-projects"]')?.textContent).toContain('may deploy');
    expect(element.querySelector('[data-testid="access-enrollment-no-project"]')?.textContent).toContain('deploys nothing');
    (element.querySelector('[data-testid="access-enrollment-project-PROJ-001"]') as HTMLInputElement).click();
    fixture.detectChanges();
    await create();

    expect(access.createEnrollment).toHaveBeenCalledWith('stage-host', 'deploy', ['PROJ-001']);
    expect(element.querySelector('[data-testid="access-run-host-command-text"]')!.textContent).toBe(runHostCommand(location.origin, ENROLLMENT_CODE));
  });

  it('drops a shown code when access is refused', async () => {
    const { fixture, element, name, kind, create } = mount();
    name('engine-host');
    kind('orchestrator');
    await create();
    expect(element.textContent).toContain(ENROLLMENT_CODE);

    access.state.set('denied');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.textContent).not.toContain(ENROLLMENT_CODE);
  });
});
