/** True when Studio (or an operator) configured an OTLP endpoint and the SDK is not disabled. */
export function telemetryEnabled(env = process.env) {
  return Boolean(env.OTEL_EXPORTER_OTLP_ENDPOINT || env.OTEL_EXPORTER_OTLP_METRICS_ENDPOINT || env.OTEL_EXPORTER_OTLP_LOGS_ENDPOINT)
    && env.OTEL_SDK_DISABLED !== 'true';
}
