import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { NotificationService } from '@studio/ui/services/notification.service';
import { TaskService } from '../../../../../services/task.service';
import type { PipelineStepConfig } from '../../../../task-pipeline';
import { PipelineStepToggleComponent } from './pipeline-step-toggle.component';

const CONFIG: PipelineStepConfig = {
  enabled: false,
  canDisable: true,
  cliType: 'claude',
  model: 'claude-haiku-4-5',
  thinkingLevel: 'low',
  mode: 'warn',
  prompt: 'Review the change.',
  condition: { when: 'tag', value: 'frontend' },
};

function setup(enabled = false, config: PipelineStepConfig | null = CONFIG) {
  const response = new Subject<unknown>();
  const setProjectPipelineStep = vi.fn().mockReturnValue(response);
  const warning = vi.fn();

  TestBed.configureTestingModule({
    imports: [PipelineStepToggleComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { setProjectPipelineStep } },
      { provide: NotificationService, useValue: { warning } },
    ],
  });

  const fixture = TestBed.createComponent(PipelineStepToggleComponent);
  fixture.componentRef.setInput('projectName', 'studio');
  fixture.componentRef.setInput('stepId', 'aspect-code-quality');
  fixture.componentRef.setInput('label', 'Code quality review');
  fixture.componentRef.setInput('enabled', enabled);
  fixture.componentRef.setInput('config', config);
  fixture.detectChanges();

  const button = () => fixture.nativeElement.querySelector('button') as HTMLButtonElement;
  return { fixture, button, response, setProjectPipelineStep, warning };
}

describe('PipelineStepToggleComponent', () => {
  it('renders a compact named switch with its current state', () => {
    const { button } = setup(true);

    expect(button().getAttribute('role')).toBe('switch');
    expect(button().getAttribute('aria-label')).toBe('Code quality review');
    expect(button().getAttribute('aria-checked')).toBe('true');
    expect(button().textContent).toContain('On');
  });

  it('optimistically toggles and preserves every config facet in the write', () => {
    const { fixture, button, setProjectPipelineStep } = setup();

    button().click();
    fixture.detectChanges();

    expect(setProjectPipelineStep).toHaveBeenCalledWith('studio', {
      stepId: 'aspect-code-quality',
      enabled: true,
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      thinkingLevel: 'low',
      mode: 'warn',
      prompt: 'Review the change.',
      condition: { when: 'tag', value: 'frontend' },
    });
    expect(button().getAttribute('aria-checked')).toBe('true');
    expect(button().getAttribute('aria-busy')).toBe('true');
    expect(button().disabled).toBe(true);
    expect(button().textContent).toContain('Saving...');
  });

  it('emits changed only after the write succeeds', () => {
    const { fixture, button, response } = setup();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);

    button().click();
    expect(changed).not.toHaveBeenCalled();

    response.next({});
    response.complete();
    fixture.detectChanges();

    expect(changed).toHaveBeenCalledOnce();
    expect(changed).toHaveBeenCalledWith(true);
    expect(button().disabled).toBe(false);
    expect(button().getAttribute('aria-busy')).toBeNull();
  });

  it('rolls back and warns when the write fails', () => {
    const { fixture, button, response, warning } = setup();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);

    button().click();
    response.error(new Error('network'));
    fixture.detectChanges();

    expect(button().getAttribute('aria-checked')).toBe('false');
    expect(button().textContent).toContain('Off');
    expect(button().disabled).toBe(false);
    expect(changed).not.toHaveBeenCalled();
    expect(warning).toHaveBeenCalledWith(
      'Code quality review could not be enabled. Try again in a moment.',
      'Pipeline step update failed',
    );
  });

  it('sends null facets when no resolved config exists', () => {
    const { button, setProjectPipelineStep } = setup(false, null);

    button().click();

    expect(setProjectPipelineStep).toHaveBeenCalledWith('studio', {
      stepId: 'aspect-code-quality',
      enabled: true,
      cliType: null,
      model: null,
      thinkingLevel: null,
      mode: null,
      prompt: null,
      condition: null,
    });
  });
});
