OTLP/HTTP protobuf requests exported by `project-templates/container-service`
(`node --import ./telemetry.mjs server.mjs`) on 2026-11-17 between 22:21 and
22:22 UTC, in export order. The service ran with
`OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=stage,agent_studio.release.id=rel-template`,
`OTEL_METRIC_EXPORT_INTERVAL=3000` and an unreachable `orders` health
dependency, and answered `/health`, `/` and `/missing` three times each.
`TelemetryOperationsReaderTests` replays them through the intake.
