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

export function hasDiff2HtmlLoaded(): boolean {
  return diff2htmlModuleCache !== null;
}

export function currentDiff2Html(): Diff2HtmlModule | null {
  return diff2htmlModuleCache;
}

export async function loadDiff2Html(): Promise<Diff2HtmlModule> {
  if (diff2htmlModuleCache) return diff2htmlModuleCache;
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
