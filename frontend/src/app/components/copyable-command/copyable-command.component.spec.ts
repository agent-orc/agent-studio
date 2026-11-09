import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { CopyableCommandComponent } from './copyable-command.component';

describe('CopyableCommandComponent', () => {
  it('shows the command as a labelled detail and copies it on request', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await TestBed.configureTestingModule({
      imports: [CopyableCommandComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCommandComponent);
    fixture.componentRef.setInput('command', 'docker compose exec api git --version');
    fixture.componentRef.setInput('testId', 'readiness-command');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="readiness-command"]')!.textContent).toContain('Command');
    expect(root.querySelector('[data-testid="readiness-command-text"]')!.textContent)
      .toBe('docker compose exec api git --version');
    await fixture.componentInstance.copy();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith('docker compose exec api git --version');
    expect(root.querySelector('[data-testid="readiness-command-copy"]')!.textContent).toContain('Copied');
    fixture.destroy();
  });
});
