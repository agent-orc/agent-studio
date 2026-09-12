# Changelog

All notable changes to this project will be documented in this file.

The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses semantic versioning for tagged releases.

The current development version is declared in [`VERSION`](VERSION), the
repository's single version source. It has not been published as a tagged
release yet.

## [Unreleased]

### Added

- Initial open source community and release-hygiene baseline.
- Local run teardown now owns and ends complete Windows and Linux process trees,
  retries worktree removal, renames persistent stale directories aside, and
  periodically sweeps orphan directories that lost their `.git` link. Worktree
  preparation failures are visible on cards and timelines, retry with bounded
  backoff, and park with a path-specific blocker after five attempts. Local run
  admission also warns when a configured project URL port already has a listener.
