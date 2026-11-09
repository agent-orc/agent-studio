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
    expect(root.querySelector('[data-testid="readiness-command-location"]')).toBeNull();
    fixture.destroy();
  });

  it('labels a configuration entry with its file and key path instead of calling it a command', async () => {
    await TestBed.configureTestingModule({
      imports: [CopyableCommandComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(CopyableCommandComponent);
    fixture.componentRef.setInput('command', '- ${STUDIO_DATA_DIR}/home:/data/home');
    fixture.componentRef.setInput('label', 'Config entry');
    fixture.componentRef.setInput('location', 'compose.override.yaml › services.api.volumes');
    fixture.componentRef.setInput('testId', 'readiness-config');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    const block = root.querySelector('[data-testid="readiness-config"]')!;
    expect(block.textContent).toContain('Config entry');
    expect(block.textContent).not.toContain('Command');
    expect(root.querySelector('[data-testid="readiness-config-location"]')!.textContent)
      .toBe('compose.override.yaml › services.api.volumes');
    expect(root.querySelector('[data-testid="readiness-config-copy"]')!.getAttribute('aria-label'))
      .toBe('Copy config entry');
    fixture.destroy();
  });
});
