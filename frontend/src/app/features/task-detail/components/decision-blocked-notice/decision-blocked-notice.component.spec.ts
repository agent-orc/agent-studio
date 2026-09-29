import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { DecisionBlockedNoticeComponent } from './decision-blocked-notice.component';
import { NotificationService } from '../../../../services/notification.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import type { TaskInfo } from '../../../../models/task.model';

async function mount(info: Partial<TaskInfo>, opened = true) {
  const navigation = { openReference: vi.fn(() => opened) };
  const notifications = { info: vi.fn() };
  await TestBed.configureTestingModule({
    imports: [DecisionBlockedNoticeComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskReferenceNavigationService, useValue: navigation },
      { provide: NotificationService, useValue: notifications },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DecisionBlockedNoticeComponent);
  fixture.componentRef.setInput('job', { id: 'dep', kind: 'task', ...info } as TaskInfo);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, navigation, notifications };
}

describe('DecisionBlockedNoticeComponent (AGT-2795)', () => {
  it('stays empty when nothing blocks the card', async () => {
    const { host } = await mount({ blockedBy: [] });
    expect(host.querySelector('[data-testid="decision-blocked-notice"]')).toBeNull();
  });

  it('names the pending decision, links it, and explains the claim guard', async () => {
    const { fixture, host, navigation } = await mount({
      blockedBy: ['AGT-2792'],
      waitsOn: {
        blocked: true, cycleDetected: false,
        items: [{ key: 'AGT-2792', resolved: true, fulfilled: false, pendingDecision: true, targetTitle: 'Stable release contract' }],
      },
    });
    const notice = host.querySelector('[data-testid="decision-blocked-notice"]');
    expect(notice?.textContent).toContain('Blocked by');
    expect(notice?.textContent).toContain('Stable release contract');
    expect(notice?.textContent).toContain('cannot be claimed or moved to Ready or In Progress');
    const link = host.querySelector('[data-testid="decision-blocked-link"]') as HTMLButtonElement;
    expect(link.textContent?.trim()).toBe('AGT-2792');
    link.click();
    fixture.detectChanges();
    expect(navigation.openReference).toHaveBeenCalledWith('AGT-2792');
  });

  it('says so when the decision card is not loaded', async () => {
    const { host, notifications } = await mount({ blockedBy: ['AGT-9'] }, false);
    (host.querySelector('[data-testid="decision-blocked-link"]') as HTMLButtonElement).click();
    expect(notifications.info).toHaveBeenCalledWith(expect.stringContaining('AGT-9'));
  });
});
