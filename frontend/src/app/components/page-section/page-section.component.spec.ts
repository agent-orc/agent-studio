import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PageSectionComponent } from './page-section.component';

/* eslint-disable @angular-eslint/component-max-inline-declarations */
@Component({
  standalone: true,
  imports: [PageSectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-section anchor="people" title="People" purpose="People with their own sign-in." [count]="6" testId="section">
      <button sectionAction type="button">Invite person</button>
      <p class="body">Rows</p>
    </app-page-section>
    <app-page-section anchor="codes" title="Enrollment codes" level="sub"><p>Codes</p></app-page-section>`,
})
class HostComponent {}

describe('PageSectionComponent', () => {
  it('renders an anchored, labelled section with purpose, count, action and body', () => {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const section = root.querySelector('[data-testid="section"]')!;

    expect(section.id).toBe('people');
    expect(section.getAttribute('aria-labelledby')).toBe('people-title');
    expect(section.querySelector('h3#people-title')!.textContent).toContain('People');
    expect(section.querySelector('.count-badge')!.textContent!.trim()).toBe('6');
    expect(section.querySelector('.page-section__purpose')!.textContent).toBe('People with their own sign-in.');
    expect(section.querySelector('.page-section__actions button')!.textContent).toBe('Invite person');
    expect(section.querySelector('.page-section__body .body')).toBeTruthy();
  });

  it('renders a part of a section one heading level down and without a count', () => {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const sub = (fixture.nativeElement as HTMLElement).querySelector('#codes')!;

    expect(sub.querySelector('h4#codes-title')).toBeTruthy();
    expect(sub.querySelector('.count-badge')).toBeNull();
    expect(sub.classList.contains('page-section--sub')).toBe(true);
  });
});
