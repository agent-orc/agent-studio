import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { grantsOf } from '../../models/access-view.model';
import { ACCESS_PROJECTS, accessOverview } from '../../testing/access-fixtures';
import { AccessGrantsComponent } from './access-grants.component';

describe('AccessGrantsComponent', () => {
  function mount(inputs: Record<string, unknown>) {
    TestBed.configureTestingModule({ imports: [AccessGrantsComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(AccessGrantsComponent);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }
  const chips = (root: HTMLElement) => [...root.querySelectorAll('.chip')].map(chip => chip.getAttribute('title'));

  it('shows the role and its scope, and marks a role held through a group', () => {
    const root = mount({ grants: grantsOf('user', 'usr_dev', accessOverview(), ACCESS_PROJECTS) });
    expect(chips(root)).toEqual(['Developer · Website', 'Approver · Website · production · via Team Website']);
    expect(root.querySelectorAll('.chip')[1].getAttribute('data-emphasis')).toBe('quiet');
  });

  it('emphasises the Owner role', () => {
    const owner = mount({ grants: grantsOf('user', 'usr_owner', accessOverview(), ACCESS_PROJECTS) });
    expect(owner.querySelector('.chip')!.getAttribute('data-emphasis')).toBe('strong');
  });

  it('keeps the chips on one line in a grid, without the group note, and counts those that wrapped out', async () => {
    const grants = grantsOf('user', 'usr_dev', accessOverview(), ACCESS_PROJECTS);
    TestBed.configureTestingModule({ imports: [AccessGrantsComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(AccessGrantsComponent);
    fixture.componentRef.setInput('grants', grants);
    fixture.componentRef.setInput('fit', true);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.grants--fit')).toBeTruthy();
    expect(chips(root)).toEqual(['Developer · Website', 'Approver · Website · production']);

    // A layout where the second chip wrapped below the one visible line.
    const list = root.querySelector('.grants__list') as HTMLElement;
    Object.defineProperty(list, 'scrollHeight', { configurable: true, value: 48 });
    Object.defineProperty(list, 'clientHeight', { configurable: true, value: 22 });
    Object.defineProperty(list.children[1], 'offsetTop', { configurable: true, value: 26 });
    fixture.componentRef.setInput('grants', [...grants]);
    fixture.detectChanges();
    await new Promise<void>(resolve => queueMicrotask(resolve));
    fixture.detectChanges();
    expect(root.querySelector('.grants__more .chip')!.getAttribute('title')).toBe('+1');
  });

  it('says so when there is no role', () => {
    const root = mount({ grants: [], emptyLabel: 'No access' });
    expect(root.querySelector('.grants__none')!.textContent).toBe('No access');
  });
});
