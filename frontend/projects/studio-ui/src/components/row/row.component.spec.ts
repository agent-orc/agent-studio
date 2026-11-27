import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { RowComponent } from './row.component';

describe('RowComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
      imports: [RowComponent],
    });
  });

  it('constructs and exposes compact as the default variant', () => {
    const fixture = TestBed.createComponent(RowComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.variant()).toBe('compact');
    expect(fixture.componentInstance.interactive()).toBe(false);
  });

  it('reflects variant onto the host data-variant attribute', () => {
    const fixture = TestBed.createComponent(RowComponent);
    fixture.componentRef.setInput('variant', 'default');
    fixture.detectChanges();
    expect(fixture.nativeElement.getAttribute('data-variant')).toBe('default');
  });

  it('marks interactive rows with the data-interactive flag', () => {
    const fixture = TestBed.createComponent(RowComponent);
    fixture.componentRef.setInput('interactive', true);
    fixture.detectChanges();
    expect(fixture.nativeElement.hasAttribute('data-interactive')).toBe(true);
  });
});
