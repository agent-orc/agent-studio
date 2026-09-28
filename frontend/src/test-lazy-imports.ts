/**
 * Lets every spec settle the product's lazy imports before Vitest tears the
 * file's environment down.
 *
 * Components such as the git pane and the diff content view start
 * `loadDiff2Html()` when they render and do not await it. A spec that renders
 * them can therefore finish while the diff2html import chain (about ten ESM
 * files) is still resolving. Vitest then tears the environment down, the
 * dangling import rejects as an unhandled error, and the next spec in the same
 * worker that imports diff2html fails to collect with "Cannot load
 * /node_modules/diff2html/lib-esm/diff-parser.js ... after the environment was
 * torn down". The window is invisible on a fast workstation and wide on a
 * loaded gate host, where it failed the 0.9.3 promotion train.
 *
 * Registered through `setupFiles` in the Angular unit-test target.
 */
import { afterEach } from 'vitest';
import { whenDiff2HtmlSettled } from './app/utils/diff2html-lazy';

afterEach(async () => {
  await whenDiff2HtmlSettled();
});
