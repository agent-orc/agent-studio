# AGT-3185 networked service access status

- Result SHA (SHA-256 of the uncommitted delivery patch, excluding this report): `21d63e7c656c6a4c63585349de109334a6746d7ee9020b13889271cd6db34836`.
- Base commit: `fc7bc1c24fee4e63edd626f479ec6701df0513e4`. The managed task platform owns the delivery commit.
- Dossier decision used: §19 D15 option A, a dedicated Studio VM, remains the target deployment topology. For service TLS, enrollment uses the host system trust store. A private issuing CA must be installed there before enrollment.
- Implemented: explicit scopes for the current `TaskServerClient` routes, enrolled runner ID ownership on versioned runner routes, service client registration for enrolled IDs, `agent-host --enroll`, HTTPS engine Compose configuration, host migration instructions, and an isolated HTTPS Compose smoke.
- The probe's `/api/v1/reviews/claims` path is not a current `TaskServerClient` call. The current review claim route is `/api/v1/runners/{runnerId}/review-claims`, which is covered.
- Verification: backend and runner test projects built with zero errors. The first solution build exposed a missing required property in the new TLS test fixture; it was corrected before the successful focused builds.
- Focused filters: backend `FullyQualifiedName~AccessSecurityTests|FullyQualifiedName~NetworkedSecurityEndpointTests|FullyQualifiedName~RealTlsTransportTests|FullyQualifiedName~MonolithOrchestrationEndpointTests|FullyQualifiedName~StudioV1LegacyRouteAliasTests` passed 108 tests; runner `FullyQualifiedName~RunnerEnrollmentCommandTests` passed 2 tests.
- Guard filter `FullyQualifiedName~.Architecture.|Category=Guard` ran on all eight .NET test projects. The backend project passed 82 tests; the other seven had no matching tests. Backend guards were repeated after the final test build and passed 82 tests.
- Lint: `npm --prefix frontend run lint` passed. `bash -n` and both Compose configuration checks passed. `deploy/compose/networked-runner-smoke/run.sh` passed against the temporary networked monolith and Caddy edge, including HTTPS CA trust, enrollment, project assignment, lease acquisition, and fenced completion.
