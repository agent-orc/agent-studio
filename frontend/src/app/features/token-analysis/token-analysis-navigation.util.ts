import { withKvSegment, withRouteSegment } from '../../services/url-hash.util';

/**
 * Entry links into the token analysis (AGT-3267). Deliberately free of the
 * filter and aggregation modules: the board popover and the card detail load
 * eagerly, the analysis view itself does not.
 */

/** Route of the workspace token analysis page inside the Token usage settings. */
export const TOKEN_ANALYSIS_ROUTE = '/workspace/settings/tokens/analysis';

/** Hash key-value segment that owns the analysis filter state. */
export const ANALYSIS_HASH_KEY = 'ta';

/** Hash that opens the workspace token analysis focused on one card's drill-down. */
export function tokenAnalysisCardHash(currentHash: string, project: string, jobId: string): string {
  // Same encoding as encodeAnalysisFilters with only the drill-down card set.
  const filters = encodeURIComponent(new URLSearchParams({ card: `${project}/${jobId}` }).toString());
  return withKvSegment(withRouteSegment(currentHash, TOKEN_ANALYSIS_ROUTE), ANALYSIS_HASH_KEY, filters);
}

/**
 * Open the card drill-down from anywhere (board token popover, card detail).
 * Assigning the hash fires `hashchange`, which the workspace overlays service
 * reconciles into the open Token usage analysis page.
 */
export function openCardTokenAnalysis(project: string, jobId: string): void {
  window.location.hash = tokenAnalysisCardHash(window.location.hash, project, jobId);
}
