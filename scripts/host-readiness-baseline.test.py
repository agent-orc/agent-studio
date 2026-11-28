import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("host-readiness-baseline.py")
AT = "2026-11-28T11:50:00Z"


def capture(kind, data, profile="task-server-a", scope="deployment"):
    return {"kind": kind, "data": data, "profile": profile, "scope": scope,
            "release": "1.2.3", "capturedAt": AT}


class HostReadinessBaselineTests(unittest.TestCase):
    def run_export(self, data, **kwargs):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "capture.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return subprocess.run([sys.executable, SCRIPT, path], capture_output=True, text=True, **kwargs)

    def export(self, *captures):
        result = self.run_export(captures)
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)

    def test_supplied_only_and_local_capture_never_count_as_deployed(self):
        report = self.export(capture("phase-samples", [{"sampleId": "build", "concurrency": 1}],
                                     "worktree", "isolated-worktree"))
        self.assertEqual(["P1", "P2", "card-brief"], [row["id"] for row in report["supplied"]["facts"]])
        self.assertEqual(1, report["observations"][0]["facts"][0]["concurrency"])
        self.assertIn("phase-samples", report["deploymentSourcesWithoutCapture"])

    def test_role_topology_conflict_and_inventory_scope(self):
        rows = [{
            "runnerId": "review-1", "hostId": "registration-group", "instanceId": "active",
            "capabilities": [{"key": "executor:review", "isFresh": False}],
            "runtimeCapacity": {"version": 5, "allocationVersion": 3},
            "runtimeCapacityAppliedVersion": 2,
            "telemetry": {"placement": {"kind": "container", "parentMachineId": "guest",
                                        "ancestors": [{"id": "metal", "kind": "physical"},
                                                      {"id": "guest", "kind": "vm"}]}},
            "containers": [{"id": "abc", "name": "same-name"}, {"id": "def", "name": "same-name"}],
        }, {
            "runnerId": "gate-1", "hostId": "registration-group",
            "capabilities": [{"key": "gate-executor"}],
            "telemetry": {"placement": {"kind": "container", "parentMachineId": "guest",
                                        "ancestors": [{"id": "guest", "kind": "vm"}]}},
        }]
        declarations = [
            {"subjectProfile": "other", "containerId": "def", "physicalMachineId": "wrong"},
            {"subjectProfile": "task-server-a", "containerId": "abc", "physicalMachineId": "metal"},
            {"subjectProfile": "task-server-a", "runnerId": "review-1", "instanceId": "old",
             "physicalMachineId": "wrong"},
        ]
        args = [capture("task-server-roles", rows), capture("role-profile", declarations, "owner-a")]
        report = self.export(*args)
        review, gate = report["observations"][0]["facts"]
        self.assertEqual("metal", review["physicalMapping"]["physicalMachineId"])
        self.assertEqual("guest", review["topology"]["parentMachineId"])
        self.assertEqual("unresolved", gate["physicalMapping"]["status"])
        self.assertEqual(["gate"], gate["advertisedRoles"])
        self.assertEqual("metal", review["containers"][0]["physicalMapping"]["physicalMachineId"])
        self.assertEqual("unresolved", review["containers"][1]["physicalMapping"]["status"])
        self.assertEqual(3, review["desiredCapacity"]["allocationVersion"])
        self.assertEqual(2, review["runtimeCapacityAppliedVersion"])
        self.assertFalse(review["readiness"][0]["isFresh"])

        declarations.append({"subjectProfile": "task-server-a", "runnerId": "review-1",
                             "instanceId": "active", "physicalMachineId": "other-metal"})
        mapping = self.export(*args)["observations"][0]["facts"][0]["physicalMapping"]
        self.assertEqual("conflicting", mapping["status"])
        self.assertIsNone(mapping["physicalMachineId"])

    def test_conflicting_reported_ancestors_survive_receipt_join(self):
        role = {"runnerId": "review-1", "instanceId": "active", "telemetry": {"placement": {
            "kind": "container", "ancestors": [
                {"id": "metal-b", "kind": "physical"},
                {"id": "metal-a", "kind": "physical"},
                {"id": "guest", "kind": "vm"},
            ],
        }}}
        receipt = {"subjectProfile": "task-server-a", "runnerId": "review-1",
                   "instanceId": "active", "physicalMachineId": "metal-a"}
        for receipts in ([], [receipt]):
            with self.subTest(receipt_count=len(receipts)):
                report = self.export(capture("task-server-roles", [role]),
                                     capture("role-profile", receipts, "owner-a"))
                mapping = report["observations"][0]["facts"][0]["physicalMapping"]
                self.assertEqual("conflicting", mapping["status"])
                self.assertIsNone(mapping["physicalMachineId"])
                self.assertEqual(["metal-a", "metal-b"], mapping["reportedPhysicalMachineIds"])
                self.assertEqual(len(receipts), len(mapping["profileReceipts"]))

    @unittest.skipUnless(sys.platform == "linux", "address-space limit requires Linux")
    def test_rejects_projection_fanout_within_memory_limit(self):
        import resource

        def limit_memory():
            ceiling = 96 * 1024 * 1024
            resource.setrlimit(resource.RLIMIT_AS, (ceiling, ceiling))

        # All arrays fit the row cap and the raw input fits the byte cap. The
        # same inventory ID can appear in multiple independently reported views.
        inventory = [{"id": "container-a"}] * 128
        roles = [{"runnerId": f"review-{i}", "containers": inventory} for i in range(128)]
        receipts = [{"subjectProfile": "task-server-a", "containerId": "container-a",
                     "physicalMachineId": f"metal-{i}"} for i in range(128)]
        result = self.run_export(
            [capture("task-server-roles", roles), capture("role-profile", receipts, "owner-a")],
            preexec_fn=limit_memory, timeout=15,
        )
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)
        self.assertEqual("Capture exceeds 2 MiB after projection. Select a smaller sample.\n",
                         result.stderr)

    def test_projects_queue_phases_and_secrets(self):
        secret = "private metadata sentinel"
        digest = "a" * 64
        report = self.export(
            capture("task-server-quotas", [{"provider": "codex", "subscriptionKey": digest, "hostId": "r1",
                                           "error": secret, "windows": [{"usedPct": 25, "resetLabel": secret}]}]),
            capture("task-server-placement", [{"projectId": "p", "pinnedRunnerId": "r2", "maxParallelism": 1}]),
            capture("task-server-queue", {"count": 1, "tasks": [{"taskKey": "AGT-1", "ageMinutes": 25, "title": secret}]}),
            capture("studio-gates", {"capacity": {"slots": 2}, "use": {"busy": 1, "total": 2}}),
            capture("studio-integration", {"rows": [{"project": "p", "recentIntegrations": [
                {"stagesMs": {"build": 53, "restore": None, secret: 91}, "secondCycleReasons": [secret]}]}]}),
            capture("task-server-roles", [{"runnerId": "r1", "capabilities": [
                {"key": "provider-auth:codex", "detail": secret, "identity": secret}],
                "release": {"imageRef": secret}, "telemetry": {"reviewPlane": {"cpuMax": "max 100000"}}}]),
        )
        self.assertNotIn(secret, json.dumps(report))
        quotas, pins, queue, gates, phases, roles = [row["facts"] for row in report["observations"]]
        self.assertEqual(digest, quotas[0]["subscriptionKey"])
        self.assertEqual("r1", quotas[0]["reportingRunnerId"])
        self.assertTrue(quotas[0]["probeFailed"])
        self.assertEqual("r2", pins[0]["pinnedRunnerId"])
        self.assertEqual(25, queue["tasks"][0]["ageMinutes"])
        self.assertIn("task creation", queue["ageBasis"])
        self.assertEqual(1, gates["use"]["busy"])
        self.assertEqual("max 100000", roles[0]["reviewCgroup"]["cpuMax"])
        stages = phases["projects"][0]["recentIntegrations"][0]["stagesMs"]
        self.assertEqual(53, stages["build"])
        self.assertIsNone(stages["restore"])

    def test_rejects_oversized_malformed_and_undated_inputs(self):
        for data in ([capture("task-server-roles", [{}] * 129)], [capture("studio-gates", [])],
                     [capture("task-server-roles", "x" * (2 * 1024 * 1024))],
                     [capture("task-server-roles", [{"containers": []}, {"containers": [None]}])],
                     [dict(capture("task-server-roles", []), capturedAt="undated")]):
            with self.subTest(size=len(str(data))):
                result = self.run_export(data)
                self.assertEqual(2, result.returncode)
                self.assertEqual("", result.stdout)

    def test_monolith_route_shapes_and_ready_entry_age(self):
        report = self.export(
            capture("monolith-quotas", {"subscriptions": [
                {"cliType": "claude", "window": "Current session", "usedPercent": 7}]}),
            capture("monolith-placement", {"project-a": {"executionLocation": "runner:review-1",
                                                          "maxParallelism": 1}}),
            capture("monolith-queue", {"observedAt": AT, "waitingTaskCount": 2, "items": [
                {"taskKey": "AGT-1", "enteredLaneAt": "2026-11-28T11:20:00Z"},
                {"taskKey": "AGT-2", "enteredLaneAt": "2026-11-28T11:52:00Z"}]}),
        )
        quotas, pins, queue = [row["facts"] for row in report["observations"]]
        self.assertEqual("Current session", quotas[0]["window"])
        self.assertEqual("project-a", pins[0]["projectId"])
        self.assertEqual("runner:review-1", pins[0]["executionLocation"])
        self.assertEqual(30, queue["tasks"][0]["ageMinutes"])
        self.assertIsNone(queue["tasks"][1]["ageMinutes"])


if __name__ == "__main__":
    unittest.main()
