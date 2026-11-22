import { logs, SeverityNumber } from '@opentelemetry/api';

const SEVERITY = {
  debug: SeverityNumber.DEBUG, info: SeverityNumber.INFO, warn: SeverityNumber.WARN, error: SeverityNumber.ERROR,
};
const ORDER = ['debug', 'info', 'warn', 'error'];

/**
 * Writes one JSON line to stdout and emits the same record as an OpenTelemetry
 * log, which reaches Studio log search when telemetry is enabled. Without an SDK
 * the OpenTelemetry logger is a no-op.
 */
export function log(level, message, attributes = {}, { env = process.env, write = line => process.stdout.write(line) } = {}) {
  const threshold = ORDER.indexOf(env.LOG_LEVEL ?? 'info');
  if (ORDER.indexOf(level) < (threshold < 0 ? 1 : threshold)) return;
  write(JSON.stringify({ time: new Date().toISOString(), level, message, ...attributes }) + '\n');
  logs.getLogger('studio-container-service').emit({
    severityNumber: SEVERITY[level] ?? SeverityNumber.INFO, severityText: level.toUpperCase(), body: message, attributes,
  });
}
