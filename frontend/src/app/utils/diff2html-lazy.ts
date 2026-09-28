// Shared lazy loader for diff2html. The library is large enough that
// diff surfaces should load it only when a small or explicitly revealed
// diff actually needs HTML rendering.
export interface Diff2HtmlOptions {
  drawFileList: boolean;
  outputFormat: 'line-by-line' | 'side-by-side';
  matching: 'lines';
  colorScheme: number;
}

export type Diff2HtmlRenderer = (diff: string, opts: Diff2HtmlOptions) => string;

export interface Diff2HtmlModule {
  readonly html: Diff2HtmlRenderer;
  readonly darkScheme: number;
}

let diff2htmlModuleCache: Diff2HtmlModule | null = null;
let diff2htmlLoadInFlight: Promise<Diff2HtmlModule> | null = null;

export function hasDiff2HtmlLoaded(): boolean {
  return diff2htmlModuleCache !== null;
}

export function currentDiff2Html(): Diff2HtmlModule | null {
  return diff2htmlModuleCache;
}

/**
 * Loads diff2html once. Concurrent callers share the same in-flight import;
 * a failed import is retried by the next caller.
 */
export function loadDiff2Html(): Promise<Diff2HtmlModule> {
  if (diff2htmlModuleCache) return Promise.resolve(diff2htmlModuleCache);
  diff2htmlLoadInFlight ??= importDiff2Html().finally(() => {
    diff2htmlLoadInFlight = null;
  });
  return diff2htmlLoadInFlight;
}

/**
 * Resolves once no diff2html import is in flight (immediately when nothing is
 * loading). Components start the load without awaiting it; the unit-test
 * setup awaits this after every test so a spec never ends while the import
 * chain is still resolving. Vitest tears the file's environment down at that
 * point, the dangling import rejects, and a later spec in the same worker that
 * needs diff2html inherits the poisoned module ("Cannot load .../diff-parser.js
 * ... after the environment was torn down"). Seen on the loaded release gate
 * host, never on a fast workstation.
 */
export function whenDiff2HtmlSettled(): Promise<void> {
  return diff2htmlLoadInFlight
    ? diff2htmlLoadInFlight.then(() => undefined, () => undefined)
    : Promise.resolve();
}

async function importDiff2Html(): Promise<Diff2HtmlModule> {
  const [main, types] = await Promise.all([
    import('diff2html/lib-esm/diff2html.js'),
    import('diff2html/lib-esm/types.js'),
  ]);
  diff2htmlModuleCache = {
    html: main.html as unknown as Diff2HtmlRenderer,
    darkScheme: types.ColorSchemeType.DARK as unknown as number,
  };
  return diff2htmlModuleCache;
}
