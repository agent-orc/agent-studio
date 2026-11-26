import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ChipComponent } from './chip.component';

describe('ChipComponent', () => {
  function mount(inputs: Record<string, unknown>) {
    TestBed.configureTestingModule({ imports: [ChipComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(ChipComponent);
    for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders one segment for a plain label', () => {
    const root = mount({ label: 'Website' });
    expect(root.querySelector('.chip__label')!.textContent).toBe('Website');
    expect(root.querySelector('.chip__detail')).toBeNull();
    expect(root.querySelector('.chip')!.getAttribute('data-emphasis')).toBe('default');
  });

  it('renders label, detail and note as segments and keeps the full value as the tooltip', () => {
    const root = mount({ label: 'Viewer', detail: 'Mobile App', note: 'via Team Website', emphasis: 'quiet', testId: 'grant' });
    const chip = root.querySelector('[data-testid="grant"]')!;
    expect(chip.querySelector('.chip__detail')!.textContent).toBe('Mobile App');
    expect(chip.querySelector('.chip__note')!.textContent).toBe('via Team Website');
    expect(chip.getAttribute('title')).toBe('Viewer · Mobile App · via Team Website');
    expect(chip.getAttribute('data-emphasis')).toBe('quiet');
  });
});
