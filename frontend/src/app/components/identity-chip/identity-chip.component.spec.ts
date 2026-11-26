import { ComponentFixture, TestBed } from '@angular/core/testing';
import { IdentityChipComponent } from './identity-chip.component';

describe('IdentityChipComponent', () => {
  let fixture: ComponentFixture<IdentityChipComponent>;
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [IdentityChipComponent] });
    fixture = TestBed.createComponent(IdentityChipComponent);
  });

  it('shows the token name and the kind icon, not the id', () => {
    fixture.componentRef.setInput('id', 'service-account:sa_62c9a37021db4c9e8f10');
    fixture.componentRef.setInput('tokenName', 'operator session');
    fixture.detectChanges();
    expect(text()).toContain('operator session');
    expect(text()).toContain('🔑');
    expect(text()).not.toContain('sa_62c9');
    expect(fixture.componentInstance.tooltip()).toContain('service-account:sa_62c9a37021db4c9e8f10');
  });

  it('shows the short fallback form without a name', () => {
    fixture.componentRef.setInput('id', 'service-account:sa_62c9a37021db4c9e8f10');
    fixture.detectChanges();
    expect(text()).toContain('sa·8f10');
    expect(text()).not.toContain('62c9a37021');
    expect(fixture.nativeElement.querySelector('[data-fallback="true"]')).not.toBeNull();
  });

  it('prefixes the tooltip with the role', () => {
    fixture.componentRef.setInput('id', 'rainer-m');
    fixture.componentRef.setInput('name', 'Rainer M');
    fixture.componentRef.setInput('tooltipPrefix', 'Owner');
    fixture.detectChanges();
    expect(fixture.componentInstance.tooltip().startsWith('Owner\n')).toBe(true);
  });
});
