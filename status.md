# AGT-3187 Linux Studio follow-up

- Result SHA: `ad569d76a298be8740ad7b883369c72f29c0c3a0` (review fix). The prior delivery `94203a9fa` was replayed as `ee2fbc0eb` on `origin/develop` at `acb056b20`, keeping the history linear.
- Implemented the response-level sandbox option: registered project URL responses send `Content-Security-Policy: sandbox allow-scripts; form-action 'none'`, including direct navigation. The sandbox gives preview documents an opaque origin without relying on the iframe attribute.
- Root-only imports now require a Git checkout and record its path as `RepositoryPath` when ready. Missing `.git` is reported as `missing-checkout`.
- Updated the multi-machine setup guide for these behaviors.
- Verification passed: `dotnet build agent-taskboard.sln --no-restore` (0 errors); focused backend class filter `ProjectPathImportTests|ProjectUrlPreviewProxyTests|PublicDemoExecutionProfileTests` (13 passed); guard filter `FullyQualifiedName~.Architecture.|Category=Guard` on backend, runner, and Task Server test projects (82 matching backend tests passed); `FullyQualifiedName~Architecture` on backend (103 passed) and Task Server (5 passed); focused frontend unit specs for project URL preview, header quota, and pane toggle (42 passed); focused Playwright `project-url-preview-in-place.spec.ts` (1 passed); `npm --prefix frontend run lint`; `git diff --check`.
