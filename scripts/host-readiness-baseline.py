#!/usr/bin/env python3
"""Project a bounded host baseline from explicitly selected, saved metadata."""

import argparse
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import sys

MAX_BYTES = 2 * 1024 * 1024
MAX_ROWS = 128
TOKEN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.:+-]{0,127}\Z")
SHA = re.compile(r"(?:[a-f0-9]{40}|[a-f0-9]{64})\Z")
DIGEST = re.compile(r"[a-f0-9]{64}\Z")
CPU_MAX = re.compile(r"(?:max|[0-9]{1,15}) [0-9]{1,15}\Z")
LABEL = re.compile(r"[A-Za-z0-9 ()-]{1,80}\Z")
PROJECT_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.+ ()-]{0,127}\Z")

SOURCES = {
    "task-server-roles": "Standalone Task Server GET /api/v1/management/remote-hosts",
    "monolith-roles": "Studio monolith GET /api/v1/management/remote-hosts",
    "task-server-quotas": "Standalone Task Server GET /api/v1/quota/readings",
    "monolith-quotas": "Studio monolith GET /api/operator/coding-capacity (cached subscriptions)",
    "task-server-placement": "Standalone Task Server GET /api/v1/projects/{projectId}/placement",
    "monolith-placement": "Studio monolith GET /api/projects/settings",
    "task-server-queue": "Standalone Task Server GET /api/v1/studio/runner/queue-starvation",
    "monolith-queue": "Studio monolith GET /api/runner/queue-starvation",
    "studio-gates": "Studio GET /api/system/gate-capacity",
    "studio-integration": "Studio GET /api/gate-economics",
    "role-profile": "Owner-supplied redacted deployed service and topology metadata",
    "phase-samples": "Selected pipeline or local baseline phase receipts",
}
ROLE_FACT_OWNERS = {
    "release readiness heldCapabilities": "Runner advertisement and receiving authority capability projection",
    "admission desiredCapacity projectPolicy": "Receiving authority policy snapshot",
    "effectiveMaxParallelism runtimeCapacityAppliedVersion": "Runner acknowledgement recorded by receiving authority",
    "telemetry claimLoadGate reviewCgroup topology containers structureContainers": "Reporting runner telemetry",
    "physicalMapping": "Reported placement and explicitly keyed owner topology receipts",
}
ROLE_KEYS = {
    "executor:coding": "coding", "executor:review": "review", "gate-executor": "gate",
    "integration-executor": "integration", "post-work-executor": "post",
}

INTEGRATION_STAGES = (
    "queueWait", "dispatchQueueWait", "gateQueueWait", "bundlePreparation", "bundleTransfer",
    "sourceLockWait", "sourceObjects", "sourceCheckout", "restore", "build", "releaseBuildCheck",
    "frontendBuild", "verdictHandBack", "merge", "push", "reviewToPushed",
)

SUPPLIED = [
    {"id": "P1", "date": "2026-11-27", "physicalLabel": "A", "coresReported": 12,
     "codingSlotsReported": 10, "reviewSlotsReported": 5, "workerCpuQuotaPercentReported": 141,
     "loadRange": [100, 180], "peakBuildMinutes": 21, "usualBuildMinutes": [1, 2]},
    {"id": "P2", "date": "2026-11-27", "physicalLabel": "B", "coresReported": 24,
     "memoryGBReported": 49, "reviewContainersReported": 3, "gateContainersReported": 1,
     "loadBeforeNight": 0.03},
    {"id": "card-brief", "observedAt": "2026-11-28T11:50:00Z",
     "A": {"coresReported": 12, "cpuPercent": 100, "loadMedian": 143,
           "medianWindowHours": 12, "codingSessions": 14, "taskServer": True},
     "B": {"coresReportedForVm": 24, "cpuPercentReportedForVm": 3,
           "reviewContainersReported": 3, "gateContainersReported": 1}},
]


def scalar(value, rule):
    if value is None:
        return None
    if isinstance(rule, re.Pattern):
        return value if isinstance(value, str) and rule.fullmatch(value) else None
    if isinstance(rule, tuple):
        return value if value in rule else None
    if rule == "time" and isinstance(value, str) and len(value) <= 40:
        try:
            parsed = datetime.fromisoformat(value)
            if "T" in value and parsed.tzinfo is not None:
                return parsed.astimezone(timezone.utc).isoformat()
        except (ValueError, OverflowError):
            pass
    if rule == "number" and type(value) in (int, float) and 0 <= value <= 10**18 and math.isfinite(value):
        return value
    if rule == "bool" and type(value) is bool:
        return value
    return None


def fields(value, *, ids="", numbers="", times="", booleans="", **rules):
    value = value if isinstance(value, dict) else {}
    for keys, rule in ((ids, TOKEN), (numbers, "number"), (times, "time"), (booleans, "bool")):
        rules.update(dict.fromkeys(keys.split(), rule))
    return {key: scalar(value.get(key), rule) for key, rule in rules.items()}


def rows(value):
    if value is None:
        return None
    if not isinstance(value, list) or any(not isinstance(row, dict) for row in value):
        raise ValueError("expected a metadata array of objects")
    if len(value) > MAX_ROWS:
        raise ValueError("metadata array exceeds row limit; select a bounded sample")
    return value


def project_rows(value, project):
    items = rows(value)
    return None if items is None else [project(item) for item in items]


def role(row):
    telemetry = row.get("telemetry") or {}
    placement = telemetry.get("placement") or {}
    structure = telemetry.get("structure") or {}
    ancestors = project_rows(placement.get("ancestors"), lambda item: fields(
        item, ids="id", kind=("vm", "physical")))
    physical = {item["id"] for item in ancestors or [] if item["kind"] == "physical" and item["id"]}
    topology = fields(placement, ids="containerName parentMachineId kernelId machineName machineVirtualization",
                      kind=("physical", "vm", "container", "unknown"))
    if topology["kind"] == "physical" and not ancestors and topology["machineName"]:
        physical.add(topology["machineName"])
    topology.update(ancestors=ancestors, physicalMachineId=next(iter(physical)) if len(physical) == 1 else None)

    readiness = project_rows(row.get("capabilities"), lambda item: fields(
        item, ids="key category advertisedStatus healthState", booleans="isFresh",
        times="advertisedAt freshUntil expiresAt lastRealSuccessAt"))
    roles = sorted({ROLE_KEYS[item["key"]] for item in readiness or [] if item["key"] in ROLE_KEYS})
    reported_role = scalar(structure.get("role"), ("coding", "review", "gate", "integration", "post", "deploy"))
    containers = row.get("containers")
    if containers is None:
        containers = telemetry.get("containers")
    policy = row.get("projectPolicy") or {}
    allowed = policy.get("allowedProjectIds")
    if allowed is not None and (not isinstance(allowed, list) or len(allowed) > MAX_ROWS):
        raise ValueError("invalid project allowlist")
    return {
        **fields(row, ids="runnerId hostId instanceId runnerVersion status",
                 numbers="protocolVersion roleMaxParallelism effectiveMaxParallelism runtimeCapacityAppliedVersion activeGateCount",
                 times="registeredAt lastSeenAt runtimeCapacityAppliedAt"),
        "advertisedRoles": roles,
        "reportedRole": reported_role,
        "release": fields(row.get("release"), ids="releaseId version", times="builtAt", commit=SHA),
        "readiness": readiness,
        "heldCapabilities": project_rows(row.get("heldCapabilities"), lambda item: fields(
            item, ids="key state", times="observedAt")),
        "admission": fields(row.get("hostAdmission"), ids="admissionState", times="automaticDrainAt operatorDrainAt"),
        "desiredCapacity": fields(row.get("runtimeCapacity"), ids="hostId rampStrategy",
                                  numbers="maxParallelism targetLoadPercent version allocationVersion", times="updatedAt"),
        "projectPolicy": {
            **fields(policy, ids="hostId", numbers="version", booleans="allowAllProjects", times="updatedAt"),
            "allowedProjectIds": None if allowed is None else [scalar(item, TOKEN) for item in allowed],
        },
        "telemetry": fields(telemetry, times="observedAt", ids="taskServerConnectionStatus",
                            numbers="cpuCores activeSlots load1 load5 load15 cpuPercent memoryUsedBytes memoryTotalBytes "
                                    "diskFreeBytes diskTotalBytes cpuStealPercent ioWaitPercent swapInBytesPerSecond swapOutBytesPerSecond"),
        "claimLoadGate": fields(telemetry.get("claimLoadGate"), ids="state", times="since",
                                numbers="loadPerCore threshold sustainedSeconds activeSlots"),
        "reviewCgroup": fields(telemetry.get("reviewPlane"), times="observedAt", cpuMax=CPU_MAX,
                               numbers="planeCpuCores cpuQuotaPercent hostCores workerEnvelopeCores "
                                       "workerEnvelopeCpuQuotaPercent currentCeiling throttledShare",
                               booleans="sustainedThrottling"),
        "topology": topology,
        "containers": project_rows(containers, lambda item: fields(
            item, ids="id name state", imageDigest=re.compile(r"sha256:[a-f0-9]{64}\Z"))),
        "structureContainers": project_rows(structure.get("studioContainers"), lambda item: fields(
            item, ids="id role", numbers="restarts uptimeSeconds")),
    }


def quota(row):
    return {
        **fields(row, provider=("claude", "codex"), subscriptionKey=DIGEST,
                 times="observedAt probeFailedAt"),
        "reportingRunnerId": scalar(row.get("hostId"), TOKEN),
        "probeFailed": bool(row.get("error")),
        # The wire historically names the reporting runner 'hostId'.
        "windows": project_rows(row.get("windows"), lambda item: fields(
            item, label=LABEL, numbers="usedPct", times="resetAt")),
    }


def role_profile(row):
    return {
        **fields(row, ids="subjectProfile runnerId hostId instanceId containerId vmId physicalMachineId "
                         "releaseId runnerVersion rampStrategy",
                 role=("coding", "review", "gate", "integration", "post", "deploy", "task-server", "web"),
                 gateTransport=("local", "ssh", "pull", "claimed"),
                 numbers="onlineCores maxParallelism activeSlots workerCpuQuotaPercent workerMemoryMaxBytes "
                         "allocationVersion appliedVersion targetLoadPercent loadPerCoreThreshold "
                         "loadSustainSeconds workerCpuBurst gateSlots processorsPerGate physicalMemoryBytes "
                         "parentMemoryMaxBytes leafMemoryMaxBytes",
                 parentCpuMax=CPU_MAX, leafCpuMax=CPU_MAX, commit=SHA, booleans="gateSharding"),
        "accountBindings": project_rows(row.get("accountBindings"), lambda item: fields(
            item, provider=("claude", "codex"), subscriptionKey=DIGEST,
            confidence=("owner-confirmed", "observed", "unknown"), times="observedAt")),
    }


def phase_sample(row):
    return {
        **fields(row, ids="sampleId project runnerId instanceId physicalMachineId toolchain cacheCondition outcome",
                 numbers="concurrency initialQueueDepth dispatchQueueDurationMs", sourceSha=SHA, lockfileDigest=DIGEST,
                 times="queuedAt startedAt finishedAt"),
        "phases": project_rows(row.get("phases"), lambda item: fields(
            item, ids="phase status durationSource", times="queuedAt startedAt finishedAt",
            numbers="queueDurationMs executionDurationMs processTreeCpuMs peakMemoryBytes")),
    }


def integration(value):
    return {
        **fields(value, times="asOfUtc", numbers="laneWindowHours", window=("7d", "30d")),
        "projects": project_rows(value.get("rows"), lambda row: {
            **fields(row, project=PROJECT_NAME,
                     numbers="queued prepared gating readyToPublish oldestWaitingMinutes"),
            "recentIntegrations": project_rows(row.get("recentIntegrations"), lambda run: {
                **fields(run, ids="jobId outcome", times="completedAtUtc", sha=SHA,
                         numbers="sequence gateCycles", booleans="hasIncompleteGateTimings"),
                "stagesMs": fields(run.get("stagesMs"), numbers=" ".join(INTEGRATION_STAGES)),
            }),
        }),
    }


def monolith_queue(value):
    summary = fields(value, numbers="waitingTaskCount availableSlots thresholdMinutes",
                     times="observedAt oldestEnteredLaneAt")

    def item(row):
        result = fields(row, ids="taskKey", projectName=PROJECT_NAME, times="enteredLaneAt")
        observed, entered = summary["observedAt"], result["enteredLaneAt"]
        elapsed = ((datetime.fromisoformat(observed) - datetime.fromisoformat(entered)).total_seconds()
                   if observed and entered else None)
        return {**result, "ageMinutes": elapsed / 60 if elapsed is not None and elapsed >= 0 else None}

    return {"ageBasis": "Ready lane entry at the published snapshot time",
            **summary, "tasks": project_rows(value.get("items"), item)}


def project_source(kind, value):
    if kind in ("task-server-roles", "monolith-roles"):
        return project_rows(value, role)
    if kind == "task-server-quotas":
        return project_rows(value, quota)
    if kind == "role-profile":
        return project_rows(value, role_profile)
    if kind == "phase-samples":
        return project_rows(value, phase_sample)
    if kind == "task-server-placement":
        return project_rows(value, lambda row: fields(
            row, ids="projectId pinnedRunnerId", numbers="maxParallelism version", times="updatedAt"))
    if not isinstance(value, dict):
        raise ValueError("expected a source response object")
    if kind == "monolith-quotas":
        return project_rows(value.get("subscriptions"), lambda row: fields(
            row, cliType=("claude", "codex"), subscriptionKey=DIGEST, window=LABEL, ids="status",
            numbers="usedPercent elapsedPercent leftAtResetPercent", times="resetAt"))
    if kind == "monolith-placement":
        if len(value) > MAX_ROWS:
            raise ValueError("too many projects")
        return [{"projectId": scalar(key, PROJECT_NAME), **fields(
            row, ids="executionLocation executionRunner", numbers="maxParallelism",
            booleans="remoteExecutionEnabled")} for key, row in value.items()]
    if kind == "task-server-queue":
        return {"ageBasis": "task creation time; Ready tasks with no recent attempt",
                **fields(value, numbers="count"),
                "tasks": project_rows(value.get("tasks"), lambda row: fields(
                    row, ids="taskKey projectId", times="createdAt", numbers="ageMinutes"))}
    if kind == "monolith-queue":
        return monolith_queue(value)
    if kind == "studio-gates":
        return {**fields(value, source=("stored", "configuration")),
                "capacity": fields(value.get("capacity"), numbers="slots processorsPerGate hostCores",
                                   booleans="allowOversubscription"),
                "use": fields(value.get("use"), numbers="busy total")}
    return integration(value)


def add_topology_links(observations):
    # Source profiles remain independent. An explicit receipt can cross that boundary
    # only by naming the subject profile and exact runner instance or container ID.
    receipts = {}
    for observation in observations:
        if observation["kind"] != "role-profile":
            continue
        for receipt in observation["facts"]:
            scope = observation["scope"]
            profile = receipt["subjectProfile"]
            parent = receipt["physicalMachineId"]
            if not profile or not parent:
                continue
            evidence = (parent, observation["profile"], observation["capturedAt"])
            if receipt["runnerId"] and receipt["instanceId"]:
                key = (scope, profile, "runner", receipt["runnerId"], receipt["instanceId"])
                receipts.setdefault(key, set()).add(evidence)
            if receipt["containerId"]:
                key = (scope, profile, "container", receipt["containerId"])
                receipts.setdefault(key, set()).add(evidence)

    def link(key, reported=None):
        evidence = sorted(receipts.get(key, ()))
        parents = {item[0] for item in evidence}
        if reported:
            parents.add(reported)
        return {
            "status": "mapped" if len(parents) == 1 else "conflicting" if parents else "unresolved",
            "physicalMachineId": next(iter(parents)) if len(parents) == 1 else None,
            "profileReceipts": [{"physicalMachineId": parent, "profile": profile, "capturedAt": captured_at}
                                for parent, profile, captured_at in evidence],
        }

    for observation in observations:
        if observation["kind"] not in ("task-server-roles", "monolith-roles"):
            continue
        scope = observation["scope"]
        profile = observation["profile"]
        for row in observation["facts"]:
            row["physicalMapping"] = link(
                (scope, profile, "runner", row["runnerId"], row["instanceId"]),
                row["topology"]["physicalMachineId"])
            # An inventory can be exposed by a remote runtime. Its containers
            # need their own exact-ID evidence, even if the reporting role maps.
            for container in (row["containers"] or []) + (row["structureContainers"] or []):
                container["physicalMapping"] = link((scope, profile, "container", container["id"]))


def export(captures):
    if not isinstance(captures, list):
        raise ValueError("expected an array of captures")
    observations = []
    for capture in rows(captures):
        kind = capture.get("kind")
        if not isinstance(kind, str) or kind not in SOURCES:
            raise ValueError("unknown capture kind")
        origin = fields(capture, ids="profile release", times="capturedAt",
                        scope=("deployment", "isolated-worktree"))
        if any(value is None for value in origin.values()):
            raise ValueError("capture requires profile, release, capturedAt and scope")
        payload = capture.get("data")
        if payload is None:
            raise ValueError("capture requires data; omit an unavailable capture")
        observations.append({
            "kind": kind, "source": SOURCES[kind], **origin, "facts": project_source(kind, payload),
        })
    add_topology_links(observations)
    return {
        "schemaVersion": 1,
        "supplied": {"source": "AGT-4152 production facts P1/P2 and AGT-4396 card brief", "facts": SUPPLIED},
        "roleFactOwners": {field: owner for names, owner in ROLE_FACT_OWNERS.items() for field in names.split()},
        "observations": observations,
        "deploymentSourcesWithoutCapture": [
            kind for kind in SOURCES if not any(row["kind"] == kind and row["scope"] == "deployment"
                                                for row in observations)
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path)
    args = parser.parse_args()
    try:
        with args.captures.open("rb") as stream:
            data = stream.read(MAX_BYTES + 1)
        if len(data) > MAX_BYTES:
            raise ValueError("input exceeds 2 MiB")
        result = json.dumps(export(json.loads(data)), indent=2, allow_nan=False) + "\n"
        if len(result.encode("utf-8")) > MAX_BYTES:
            raise ValueError("export exceeds 2 MiB; select a smaller sample")
    except (OSError, ValueError, TypeError, AttributeError, RecursionError):
        parser.exit(2, "Invalid or oversized capture. Check the documented metadata schema and bounds.\n")
    sys.stdout.write(result)


if __name__ == "__main__":
    main()
