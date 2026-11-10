import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CopyableTaskKeyComponent } from './copyable-task-key.component';
import { preserveOwnProperty } from '../../../testing/preserve-own-property';

describe('CopyableTaskKeyComponent', () => {
  let fixture: ComponentFixture<CopyableTaskKeyComponent>;
  let writeText: ReturnType<typeof vi.fn>;
  preserveOwnProperty(navigator, 'clipboard');

  beforeEach(async () => {
    vi.useFakeTimers();
    writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });

    await TestBed.configureTestingModule({
      imports: [CopyableTaskKeyComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(CopyableTaskKeyComponent);
    fixture.componentRef.setInput('key', 'AGT-2268');
    fixture.componentRef.setInput('label', 'AGT-2268');
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    vi.useRealTimers();
  });

  it('copies the exact key, shows feedback, and does not activate its parent', async () => {
    const parentClick = vi.fn();
    const host = fixture.nativeElement as HTMLElement;
    const button = host.querySelector<HTMLButtonElement>('button')!;
    host.addEventListener('click', parentClick);

    button.click();
    expect(writeText).toHaveBeenCalledWith('AGT-2268');
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();

    expect(parentClick).not.toHaveBeenCalled();
    expect(button.textContent).toContain('Copied');

    vi.advanceTimersByTime(2_000);
    fixture.detectChanges();
    expect(button.textContent).toContain('AGT-2268');
  });
});
