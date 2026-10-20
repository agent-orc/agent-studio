"""Regenerates the OTLP fixtures with the official opentelemetry-proto bindings.

    python3 -m pip install --target /tmp/otelpy opentelemetry-proto
    PYTHONPATH=/tmp/otelpy python3 backend.Tests/Fixtures/otlp/generate_fixtures.py

The protobuf files are SerializeToString() output of the published messages.
The JSON files follow OTLP/JSON: lowerCamelCase keys, enums as integers,
64-bit integers as strings, and trace and span ids as hex strings.
"""
import base64
import json
import os

from google.protobuf import json_format
from opentelemetry.proto.collector.logs.v1 import logs_service_pb2
from opentelemetry.proto.collector.metrics.v1 import metrics_service_pb2
from opentelemetry.proto.common.v1 import common_pb2
from opentelemetry.proto.logs.v1 import logs_pb2
from opentelemetry.proto.metrics.v1 import metrics_pb2

HERE = os.path.dirname(os.path.abspath(__file__))
# 2026-10-16T12:00:00Z and the following minutes, in Unix nanoseconds.
T0 = 1792152000 * 10**9
MINUTE = 60 * 10**9


def kv(key, value):
    item = common_pb2.KeyValue(key=key)
    if isinstance(value, bool):
        item.value.bool_value = value
    elif isinstance(value, int):
        item.value.int_value = value
    elif isinstance(value, float):
        item.value.double_value = value
    elif isinstance(value, list):
        for element in value:
            item.value.array_value.values.add(string_value=element)
    else:
        item.value.string_value = value
    return item


def metrics_request():
    request = metrics_service_pb2.ExportMetricsServiceRequest()
    resource = request.resource_metrics.add()
    resource.resource.attributes.extend([kv("service.name", "shop-api"), kv("deployment.environment", "stage")])
    scope = resource.scope_metrics.add()
    scope.scope.name = "shop.http"

    memory = scope.metrics.add(name="process.memory.usage", unit="By")
    point = memory.gauge.data_points.add(time_unix_nano=T0, as_int=5242880)
    point.attributes.append(kv("state", "used"))
    # No value at all: rejected.
    memory.gauge.data_points.add(time_unix_nano=T0)
    # FLAG_NO_RECORDED_VALUE: a staleness marker, skipped without rejection.
    memory.gauge.data_points.add(time_unix_nano=T0, as_int=1, flags=1)

    requests = scope.metrics.add(name="http.server.requests", unit="{request}")
    requests.sum.aggregation_temporality = metrics_pb2.AGGREGATION_TEMPORALITY_CUMULATIVE
    requests.sum.is_monotonic = True
    point = requests.sum.data_points.add(start_time_unix_nano=T0 - 10 * MINUTE, time_unix_nano=T0, as_double=42.0)
    point.attributes.extend([kv("http.route", "/cart"), kv("http.status_code", 200)])

    duration = scope.metrics.add(name="http.server.duration", unit="ms")
    duration.histogram.aggregation_temporality = metrics_pb2.AGGREGATION_TEMPORALITY_DELTA
    point = duration.histogram.data_points.add(
        start_time_unix_nano=T0 - MINUTE, time_unix_nano=T0, count=4, sum=100.0, min=10.0, max=40.0,
        bucket_counts=[1, 2, 1], explicit_bounds=[20.0, 30.0])
    point.attributes.append(kv("http.route", "/cart"))

    queries = scope.metrics.add(name="db.query.duration", unit="ms")
    queries.summary.data_points.add(start_time_unix_nano=T0 - MINUTE, time_unix_nano=T0, count=3, sum=30.0)

    wait = scope.metrics.add(name="queue.wait", unit="s")
    wait.exponential_histogram.aggregation_temporality = metrics_pb2.AGGREGATION_TEMPORALITY_DELTA
    wait.exponential_histogram.data_points.add(time_unix_nano=T0, count=2, sum=8.0, scale=1, zero_count=0, min=3.0, max=5.0)
    return request


def logs_request():
    request = logs_service_pb2.ExportLogsServiceRequest()
    resource = request.resource_logs.add()
    resource.resource.attributes.append(kv("service.name", "shop-api"))
    scope = resource.scope_logs.add()
    scope.scope.name = "Shop.Checkout"

    placed = scope.log_records.add(
        time_unix_nano=T0, severity_number=logs_pb2.SEVERITY_NUMBER_INFO, severity_text="Information",
        trace_id=bytes(range(1, 17)), span_id=bytes(range(1, 9)))
    placed.body.string_value = "order placed"
    placed.attributes.extend([kv("order.id", 42), kv("tags", ["new", "express"])])

    failed = scope.log_records.add(
        time_unix_nano=T0 + MINUTE, severity_number=logs_pb2.SEVERITY_NUMBER_ERROR, severity_text="Error",
        event_name="payment.failed")
    message = failed.body.kvlist_value.values.add(key="message")
    message.value.string_value = "payment failed"
    code = failed.body.kvlist_value.values.add(key="code")
    code.value.int_value = 402

    # No severity number and no event time: the text and the observed time are used.
    low = scope.log_records.add(observed_time_unix_nano=T0 + 2 * MINUTE, severity_text="warning")
    low.body.string_value = "disk low"
    return request


def to_otlp_json(message):
    value = json_format.MessageToDict(message, use_integers_for_enums=True)

    def fix(node):
        if isinstance(node, dict):
            for key in ("traceId", "spanId"):
                if key in node:
                    node[key] = base64.b64decode(node[key]).hex()
            for child in node.values():
                fix(child)
        elif isinstance(node, list):
            for child in node:
                fix(child)

    fix(value)
    return json.dumps(value, indent=2) + "\n"


def write(name, message):
    with open(os.path.join(HERE, name + ".pb"), "wb") as handle:
        handle.write(message.SerializeToString())
    with open(os.path.join(HERE, name + ".json"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(to_otlp_json(message))


write("metrics", metrics_request())
write("logs", logs_request())
