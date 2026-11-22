// OpenTelemetry bootstrap, loaded before the server with `node --import ./telemetry.mjs`.
// Studio deploys set OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL and
// OTEL_EXPORTER_OTLP_HEADERS for environments with `telemetry: true`. Without an
// endpoint the service runs without telemetry. Metrics and logs use OTLP/HTTP protobuf,
// the protocol Studio sets.
import { createRequire } from 'node:module';
import { NodeSDK } from '@opentelemetry/sdk-node';
import { PeriodicExportingMetricReader } from '@opentelemetry/sdk-metrics';
import { BatchLogRecordProcessor } from '@opentelemetry/sdk-logs';
import { OTLPMetricExporter } from '@opentelemetry/exporter-metrics-otlp-proto';
import { OTLPLogExporter } from '@opentelemetry/exporter-logs-otlp-proto';
import { HttpInstrumentation } from '@opentelemetry/instrumentation-http';
import { RuntimeNodeInstrumentation } from '@opentelemetry/instrumentation-runtime-node';
import { HostMetrics } from '@opentelemetry/host-metrics';
import { telemetryEnabled } from './telemetry-config.mjs';

let sdk = null;

export function startTelemetry(env = process.env) {
  if (sdk || !telemetryEnabled(env)) return false;
  // Studio's intake accepts metrics and logs; it does not accept traces yet.
  env.OTEL_TRACES_EXPORTER ??= 'none';
  env.OTEL_SERVICE_NAME ??= 'studio-container-service';
  // Every resource attribute is part of a series; process paths and ids would start new series at each restart.
  env.OTEL_NODE_RESOURCE_DETECTORS ??= 'env,host';
  sdk = new NodeSDK({
    metricReaders: [new PeriodicExportingMetricReader({
      exporter: new OTLPMetricExporter(),
      exportIntervalMillis: Number(env.OTEL_METRIC_EXPORT_INTERVAL || 15000),
    })],
    logRecordProcessors: [new BatchLogRecordProcessor({ exporter: new OTLPLogExporter() })],
    instrumentations: [new HttpInstrumentation(), new RuntimeNodeInstrumentation()],
  });
  sdk.start();
  // process.cpu.time and process.memory.usage feed the CPU and memory trends in Operations.
  new HostMetrics({ name: 'studio-container-service', metricGroups: ['process.cpu', 'process.memory'] }).start();
  // The HTTP instrumentation patches the shared module object; load it once through require
  // so the server's `import http from 'node:http'` sees the patched exports.
  createRequire(import.meta.url)('node:http');
  const stop = () => { sdk?.shutdown().finally(() => process.exit(0)); };
  process.once('SIGTERM', stop);
  process.once('SIGINT', stop);
  return true;
}

startTelemetry();
