import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { EscalationDetailsComponent } from './escalation-details.component';
import type { TaskInfo } from '../../../../models/task.model';

describe('EscalationDetailsComponent council follow-up (AGT-3835)', () => {
  function mount() {
    TestBed.configureTestingModule({
      imports: [EscalationDetailsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(EscalationDetailsComponent);
    fixture.componentRef.setInput('job', { id: 'DEMO-9', watchPath: '/tmp/demo' } as TaskInfo);
    fixture.componentRef.setInput('followUpMarkdown', '# Orchestrator follow-up\n\nAsk the owner.');
    fixture.detectChanges();
    return fixture;
  }

  it('keeps the maximize button out of the summary so the summary has no nested control', () => {
    const fixture = mount();
    const council = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLDetailsElement>('[data-testid="escalation-council-follow-up"]')!;
    const maximize = council.querySelector<HTMLButtonElement>('[data-testid="escalation-follow-up-maximize"]')!;

    expect(maximize).not.toBeNull();
    expect(maximize.closest('summary')).toBeNull();
    expect(council.querySelector('summary button, summary [tabindex]')).toBeNull();
  });

  it('restores a maximized section when it is closed', () => {
    const fixture = mount();
    const component = fixture.componentInstance;
    component.followUpMaximized.set(true);

    component.disclosureChanged(false);

    expect(component.followUpMaximized()).toBe(false);
  });
});
