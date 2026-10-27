import { defer, of, switchMap, timer, type MonoTypeOperatorFunction, type Observable } from 'rxjs';

/**
 * Git facts S3 (AGT-3323): card Git reads (provenance, commit lists and files,
 * the default diff, run views, the integration view) are served from the
 * backend's Git fact store and never start Git. While the store has not
 * published a fact yet, the read answers HTTP 202 with `state: "warming"` and
 * the backend queues the work with priority. The delivery claim answers 200
 * with `factsState`, which is `warming` or `stale` until its card facts are
 * published.
 */
export const GIT_FACTS_WARMING_RETRY_DELAYS_MS: readonly number[] = [500, 1000, 2000, 4000, 8000];

export function isGitFactsWarming(body: unknown): boolean {
  if (!body || typeof body !== 'object') return false;
  const record = body as { state?: unknown; factsState?: unknown };
  return record.state === 'warming' || record.factsState === 'warming' || record.factsState === 'stale';
}

/**
 * Re-issues a cold request while its answer is warming, with backoff, and
 * emits one value: the first answer that is not warming, or the last warming
 * answer once the delays are spent. Callers keep their own error handling.
 */
export function retryWhileWarming<T>(
  delaysMs: readonly number[] = GIT_FACTS_WARMING_RETRY_DELAYS_MS,
  isWarming: (body: T) => boolean = isGitFactsWarming,
): MonoTypeOperatorFunction<T> {
  return (source: Observable<T>) =>
    defer(() => {
      let attempt = 0;
      const once = (): Observable<T> =>
        source.pipe(
          switchMap((body) => {
            if (!isWarming(body) || attempt >= delaysMs.length) return of(body);
            const wait = delaysMs[attempt++];
            return timer(wait).pipe(switchMap(() => once()));
          }),
        );
      return once();
    });
}
