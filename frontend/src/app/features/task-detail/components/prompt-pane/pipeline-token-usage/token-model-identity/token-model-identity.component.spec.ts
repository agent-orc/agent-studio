import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenModelIdentityComponent } from './token-model-identity.component';

function setup(inputs: { model: string; thinkingLevel?: string | null; steps?: number }) {
  TestBed.configureTestingModule({
    imports: [TokenModelIdentityComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(TokenModelIdentityComponent);
  fixture.componentRef.setInput('model', inputs.model);
  fixture.componentRef.setInput('thinkingLevel', inputs.thinkingLevel ?? null);
  if (inputs.steps !== undefined) fixture.componentRef.setInput('steps', inputs.steps);
  fixture.detectChanges();
  return fixture;
}

describe('TokenModelIdentityComponent', () => {
  it('names the model id and the full level word, plus the shared board badge', () => {
    const fixture = setup({ model: 'claude-opus-4-8', thinkingLevel: 'medium' });
    const host = fixture.nativeElement as HTMLElement;

    expect(host.textContent).toContain('claude-opus-4-8');
    expect(host.textContent).toContain('medium');
    expect(host.getAttribute('data-thinking-level')).toBe('medium');

    const badge = host.querySelector('[data-testid="token-model-identity-badge"]');
    expect(badge?.textContent).toContain('OP4.8');
    expect(badge?.textContent).toContain('m');
  });

  it('reads an absent level as a data gap, never as a level', () => {
    const fixture = setup({ model: 'claude-opus-4-8' });
    const host = fixture.nativeElement as HTMLElement;

    expect(fixture.componentInstance.level()).toBeNull();
    expect(fixture.componentInstance.levelLabel()).toBe('level unknown');
    expect(host.getAttribute('data-thinking-level')).toBeNull();
    expect(
      host.querySelector('[data-testid="token-model-identity-level"]')?.className,
    ).toContain('tmi__level--unknown');
  });

  it('trims a blank recorded level down to unknown', () => {
    const fixture = setup({ model: 'gpt-5.6-sol', thinkingLevel: '  ' });
    expect(fixture.componentInstance.level()).toBeNull();
  });

  it('hides the fold count for a single call', () => {
    const fixture = setup({ model: 'claude-haiku-4-5', thinkingLevel: 'low', steps: 1 });
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('×');
  });

  it('shows the fold count when several calls folded into the row', () => {
    const fixture = setup({ model: 'claude-haiku-4-5', thinkingLevel: 'low', steps: 3 });
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('×3');
  });
});
