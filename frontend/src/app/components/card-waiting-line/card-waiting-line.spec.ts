import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { CardWaitingLineComponent } from './card-waiting-line';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import type { TaskInfo } from '../../models/task.model';

describe('CardWaitingLineComponent', () => {
  const openReferenceOrNotify = vi.fn();
  beforeEach(() => {
    openReferenceOrNotify.mockReset();
    TestBed.configureTestingModule({
      imports: [CardWaitingLineComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } }],
    });
  });

  it('renders the server reason and leaves a claimable card empty', () => {
    const fixture = TestBed.createComponent(CardWaitingLineComponent);
    fixture.componentRef.setInput('task', { waitingReason: 'waits for a free slot' } as TaskInfo);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('waits for a free slot');
    fixture.componentRef.setInput('task', {} as TaskInfo);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent.trim()).toBe('');
  });

  it('shows the same reason and navigable dependencies and transitive holders in detail', () => {
    const fixture = TestBed.createComponent(CardWaitingLineComponent);
    fixture.componentRef.setInput('detail', true);
    fixture.componentRef.setInput('task', {
      waitingReason: 'waits for AGT-1 (escalated)',
      waitsOn: { items: [{ key: 'AGT-1', fulfilled: false }] },
      transitiveWaiters: { count: 2, keys: ['AGT-2', 'AGT-3'] },
    } as TaskInfo);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('holds 2 cards:');
    const links = [...fixture.nativeElement.querySelectorAll('a')] as HTMLAnchorElement[];
    expect(links.map(link => link.textContent?.trim())).toEqual(['AGT-2', 'AGT-3', 'AGT-1']);
    links[0].click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-2');
  });
});
