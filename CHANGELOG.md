# Changelog

All notable changes to this project will be documented in this file.

The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses semantic versioning for tagged releases.

The current development version is declared in [`VERSION`](VERSION), the
repository's single version source. It has not been published as a tagged
release yet.

## [Unreleased]

## [0.2.0] - 2026-09-13

First tagged Stable release. It moves the Windows Stable from the untagged legacy checkout (cf1997665) to the immutable tag v0.2.0 through the release contract: build manifest, candidate and approved tag, locked dependency restore, runtime identity verification.
### Added

- Project-owned Stable release identity and restore rules, with lock-file and
  exact-pin-plus-registry-integrity validation, fixture coverage, and a legacy
  Stable first-release runbook.
- Stage M1 repository execution definitions, shared preparation manifests,
  content-addressed npm, NuGet, and Playwright caches, Linux Runner stable
  checkouts with leased subject worktrees, proposal cards, and the project
  Settings > Execution surface.
- Durable restart continuity for local and Remote runs, review aspects, and
  pipeline post-steps, including operator-visible bridge and loss events plus a
  Windows Studio release drill.
- Initial open source community and release-hygiene baseline.
- Dossier relevance-review metadata, managed review writes and history,
  configurable review-due policy, and review tags, tooltips, filtering,
  sorting, and viewer recording controls.
- Local run teardown now owns and ends complete Windows and Linux process trees,
  retries worktree removal, renames persistent stale directories aside, and
  periodically sweeps orphan directories that lost their `.git` link. Worktree
  preparation failures are visible on cards and timelines, retry with bounded
  backoff, and park with a path-specific blocker after five attempts. Local run
  admission also warns when a configured project URL port already has a listener.

### Changed

- Dossier list cards now use a wide clamped summary, anchored footer actions,
  copyable keys, and labelled lane-colour references with immediate complete
  tooltips.

### Fixed

- Stable release manifest generation and candidate preflight no longer assume
  a product-wide `backend/packages.lock.json`; Agent Studio now declares and
  commits its NuGet lock, and promotion rejects missing or stale lock state.
- Develop-to-main promotion now supplies a configurable annotated-tag identity
  independently of host Git configuration and records post-gate tag or atomic
  push failures in the durable promotion record.
- Production builds no longer load the task-detail component graph with the
  initial task board bundle, restoring release-budget headroom without raising
  the configured limit.
- Process launches across backend, retention, setup, and repository Node
  scripts now suppress Windows console windows, drain redirected CLI stderr,
  and carry repository-wide regression guards for future spawn sites.
- Public-demo execution-route inventory now includes
  `POST /api/v1/reviews/attempts/{attemptId}/reclaim` in the `Continue` path.
- Frontend release coverage is green again for the `CliAdminPanelComponent`
  smoke test, the `CliModelsPanelComponent` known-CLI grouping test, the
  `ExecutionAssignmentCardComponent` delivery-failure test, the
  `WorkspaceOverlaysComponent` smoke test, and the Dossier and Wiki
  `GlobalSearchComponent` navigation tests. The additionally exposed
  `WorkbenchTabHostComponent` catalogue-backfill test is isolated from
  persisted tab state as well.
