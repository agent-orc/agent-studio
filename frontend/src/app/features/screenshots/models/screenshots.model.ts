/**
 * Cycle 9 screenshots feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Per-job + workspace-wide screenshot listings. Files live under
 * `<job>/results/`; the relativePath always begins with `results/`.
 */

export interface TaskScreenshot {
  jobId: string;
  jobTitle: string;
  projectName: string;
  watchPath: string;
  fileName: string;
  /** Always begins with `results/`. */
  relativePath: string;
  /** Routable URL that serves this file (sub-path aware). */
  url: string;
  /** Original file metadata from bounded header inspection. */
  sizeBytes?: number;
  width?: number | null;
  height?: number | null;
  caption: string;
  /** `passed` | `failed` | `skipped` | `unknown` | null. */
  status: string | null;
  /**
   * Provenance label derived from the filename suffix: `real` (captured
   * against a live backend), `mocked` (e2e run with mocked API routes),
   * `composite` (a stitched image), `pinned` (the versioned deterministic
   * documentation workspace), or `unlabeled` (no recognised suffix).
   * Rendered text-only next to the caption so a reviewer can tell a
   * live-backend shot from a mocked-route one.
   */
  source: string;
  /**
   * For a `composite` source, the source of each stitched part
   * (e.g. `['real', 'mocked']`). Empty for every other source.
   */
  compositeParts: string[];
  localPath: string;
  timestampUtc: string;
}

export interface TaskScreenshotsResponse {
  jobId: string;
  screenshots: TaskScreenshot[];
}

export interface WorkspaceScreenshotsResponse {
  windowHours: number;
  projectFilter: string | null;
  screenshots: TaskScreenshot[];
}
