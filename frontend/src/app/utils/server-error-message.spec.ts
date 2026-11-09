import { describe, expect, it } from 'vitest';
import { serverErrorMessage } from './server-error-message';

describe('serverErrorMessage (AGT-3582)', () => {
  it('reads a sentence in error', () => {
    expect(serverErrorMessage({ status: 409, error: { error: 'The current delivery has no failure to continue.' } }, 'x'))
      .toBe('The current delivery has no failure to continue.');
  });

  it('prefers message over a code-shaped error', () => {
    expect(serverErrorMessage({
      status: 409,
      error: { error: 'batch-isolation-active', message: 'AGT-1 is a member of batch flight f-1.', flight: 'f-1' },
    }, 'x')).toBe('AGT-1 is a member of batch flight f-1.');
  });

  it('reads a plain string body and a detail field', () => {
    expect(serverErrorMessage({ error: '  Prompt is required  ' }, 'x')).toBe('Prompt is required');
    expect(serverErrorMessage({ error: { detail: 'Not allowed.' } }, 'x')).toBe('Not allowed.');
  });

  it('falls back when the body carries no text', () => {
    expect(serverErrorMessage({ status: 409, error: {} }, 'Could not continue.')).toBe('Could not continue.');
    expect(serverErrorMessage({ status: 0, error: null }, 'Could not continue.')).toBe('Could not continue.');
    expect(serverErrorMessage(undefined, 'Could not continue.')).toBe('Could not continue.');
  });
});
