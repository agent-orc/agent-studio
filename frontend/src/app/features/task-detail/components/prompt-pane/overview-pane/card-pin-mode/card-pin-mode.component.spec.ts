import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../../../models/task.model';
import { TaskService } from '../../../../../../services/task.service';
import { CardPinModeComponent } from './card-pin-mode.component';

describe('CardPinModeComponent', () => {
  it('renders a saved hard pin, rolls back a failed save, and persists a preference', async () => {
    const pending = new Subject<unknown>();
    const setJobModel = vi.fn().mockReturnValueOnce(pending).mockReturnValue(of({}));
    await TestBed.configureTestingModule({ imports: [CardPinModeComponent],
      providers: [provideZonelessChangeDetection(), { provide: TaskService, useValue: { setJobModel } }] }).compileComponents();
    const fixture = TestBed.createComponent(CardPinModeComponent);
    fixture.componentRef.setInput('job', { id: 'card', watchPath: '/project', model: 'old', pinMode: 'hard' } as TaskInfo);
    fixture.componentRef.setInput('model', 'claude-sonnet-5');
    fixture.detectChanges(); await fixture.whenStable();
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    expect(select.value).toBe('hard');
    select.value = 'preference'; select.dispatchEvent(new Event('change'));
    expect(setJobModel).toHaveBeenCalledWith('card', 'claude-sonnet-5', '/project', 'preference');
    pending.error(new Error('offline'));
    fixture.detectChanges(); await fixture.whenStable();
    expect(select.value).toBe('hard');
    expect(fixture.nativeElement.textContent).toContain('Pin mode could not be saved.');
    let saved = 0;
    fixture.componentInstance.saved.subscribe(() => saved++);
    select.value = 'preference'; select.dispatchEvent(new Event('change'));
    expect(saved).toBe(1);
    expect(fixture.componentInstance.mode()).toBe('preference');
    fixture.componentRef.setInput('disabled', true); fixture.detectChanges();
    fixture.componentInstance.change('hard');
    expect(setJobModel).toHaveBeenCalledTimes(2);
  });
});
