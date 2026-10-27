import { describe, it, expect, vi, afterEach } from 'vitest';
import { defer, of, firstValueFrom } from 'rxjs';
import { isGitFactsWarming, retryWhileWarming } from './git-facts-warming';

describe('isGitFactsWarming', () => {
  it('recognises a warming card read and a delivery claim without published facts', () => {
    expect(isGitFactsWarming({ state: 'warming', files: [] })).toBe(true);
    expect(isGitFactsWarming({ factsState: 'warming' })).toBe(true);
    expect(isGitFactsWarming({ factsState: 'stale' })).toBe(true);
  });

  it('treats published answers and non-objects as final', () => {
    expect(isGitFactsWarming({ files: [] })).toBe(false);
    expect(isGitFactsWarming({ factsState: 'ready' })).toBe(false);
    expect(isGitFactsWarming(null)).toBe(false);
    expect(isGitFactsWarming('diff --git a/x b/x')).toBe(false);
  });
});

describe('retryWhileWarming', () => {
  afterEach(() => vi.useRealTimers());

  it('re-issues the request until the store has published the fact', async () => {
    vi.useFakeTimers();
    const answers = [{ state: 'warming' }, { state: 'warming' }, { files: ['a.txt'] }];
    let requests = 0;
    const result = firstValueFrom(defer(() => of(answers[requests++])).pipe(retryWhileWarming([10, 20])));

    await vi.advanceTimersByTimeAsync(30);

    await expect(result).resolves.toEqual({ files: ['a.txt'] });
    expect(requests).toBe(3);
  });

  it('emits the last warming answer once the delays are spent', async () => {
    vi.useFakeTimers();
    let requests = 0;
    const result = firstValueFrom(defer(() => {
      requests++;
      return of({ state: 'warming' });
    }).pipe(retryWhileWarming([5])));

    await vi.advanceTimersByTimeAsync(5);

    await expect(result).resolves.toEqual({ state: 'warming' });
    expect(requests).toBe(2);
  });

  it('passes a ready answer through without a second request', async () => {
    let requests = 0;
    const answer = await firstValueFrom(defer(() => {
      requests++;
      return of({ diff: '', hasDiff: false });
    }).pipe(retryWhileWarming([5])));

    expect(answer).toEqual({ diff: '', hasDiff: false });
    expect(requests).toBe(1);
  });
});
