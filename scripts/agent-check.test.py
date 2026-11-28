"""Focused tests for the agent check wrapper. Run with python3 -B."""

from concurrent.futures import ThreadPoolExecutor
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import uuid


SCRIPT = Path(__file__).with_name("agent-check.sh")
SPEC = importlib.util.spec_from_file_location("agent_check", SCRIPT.with_suffix(".py"))
agent_check = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent_check)


class AgentCheckTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.results = Path(directory.name)
        dotnet = self.results / "dotnet"
        dotnet.write_text(
            "#!/bin/sh\n"
            'cat "$AGENT_CHECK_FIXTURE"\n'
            'exit "$AGENT_CHECK_FIXTURE_CODE"\n'
        )
        dotnet.chmod(0o700)

    def run_fixture(self, output, code, *args):
        fixture = self.results / "fixture"
        fixture.write_text(output)
        return subprocess.run(
            [SCRIPT, *args],
            cwd=self.results,
            env={
                **os.environ, "JOB_RESULTS_DIR": str(self.results),
                "PATH": f"{self.results}{os.pathsep}{os.environ['PATH']}",
                "AGENT_CHECK_FIXTURE": str(fixture), "AGENT_CHECK_FIXTURE_CODE": str(code),
            },
            capture_output=True, text=True, timeout=20, check=False,
        )

    def test_green_is_one_line_with_log(self):
        result = self.run_fixture("Build succeeded.\n    0 Error(s)\n", 0, "build")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(result.stdout.splitlines()), 1)
        self.assertRegex(result.stdout, r"agent-check build \| PASS \| \d+\.\d+s \| log: ")
        log = Path(result.stdout.split(" | log: ")[1].strip())
        self.assertEqual(log.parent, self.results / "agent-check")
        self.assertIn("    0 Error(s)", log.read_text())

    def test_build_reports_unique_compile_errors_and_caps_summary(self):
        lines = [f"File.cs({i},8): error CS1002: ; expected [Example.csproj]" for i in range(7, 31)]
        result = self.run_fixture("\n".join(lines + lines) + "\n", 1, "build")
        self.assertEqual(result.returncode, 1)
        summary = result.stdout.splitlines()
        self.assertEqual(summary[1:19], lines[:18])
        self.assertEqual(summary[19], "6 more diagnostic lines in the full log")
        self.assertEqual(len(summary), 20)

    def test_two_failures_report_each_first_assertion(self):
        result = self.run_fixture(
            "[xUnit.net 00:00:01.21]     SampleTests.First [FAIL]\n"
            "  Failed SampleTests.First [17 ms]\n"
            "  Error Message:\n"
            "   Assert.Equal() Failure: Values differ\n"
            "Expected: 2\nActual: 1\n"
            "  Stack Trace:\n"
            "     at SampleTests.First() in SampleTests.cs:line 13\n"
            "  Failed SampleTests.Second(text: \"value\") [< 1 ms]\n"
            "  Error Message:\n"
            "   Assert.True() Failure\n"
            "Expected: True\n",
            1, "test", "SampleTests", "--project", "backend",
        )
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout.splitlines()[1:], [
            "Failed SampleTests.First | Assert.Equal() Failure: Values differ",
            'Failed SampleTests.Second(text: "value") | Assert.True() Failure',
        ])

    def test_frontend_failure_uses_the_assertion_after_the_location(self):
        self.assertEqual(agent_check.failure_details([
            "1) [chromium] › e2e/example.spec.ts:13:3 › opens the panel",
            "e2e/example.spec.ts:17",
            "Error: expect(locator).toBeVisible() failed",
            "  at PanelSpec",
        ]), [
            "Failed e2e/example.spec.ts:13:3 › opens the panel | Error: expect(locator).toBeVisible() failed",
        ])

    def test_empty_filter_match_fails(self):
        result = self.run_fixture("No test matches the given testcase filter\n", 0, "test", "Missing", "--project", "backend")
        self.assertEqual(result.returncode, 1)
        self.assertIn("No test results matched the filter", result.stdout)

    def test_timeout_reports_budget_and_stops_command(self):
        would_finish = self.results / "finished"
        child = [
            sys.executable, "-c",
            "import pathlib, sys, time; time.sleep(10); pathlib.Path(sys.argv[1]).touch()",
            str(would_finish),
        ]
        with patch.object(agent_check, "host_timeout", return_value=0.2):
            code, summary = agent_check.run_check([child], 1, self.results, "agent-check build", "build")
        self.assertEqual(code, 124)
        self.assertIn("TIMEOUT after 0.2s", summary)
        self.assertFalse(would_finish.exists())

    def test_timeout_stretches_with_simulated_host_load_and_is_bounded(self):
        with patch.object(agent_check.os, "cpu_count", return_value=8):
            for load, expected in [(3, 300), (12, 450), (64, 1200)]:
                with (
                    self.subTest(load=load),
                    patch.object(agent_check.os, "getloadavg", return_value=(load, 0, 0)),
                    patch.object(agent_check.time, "monotonic", return_value=1000),
                    patch.object(agent_check, "run_command", return_value=(124, True)) as run,
                ):
                    _, summary = agent_check.run_check(
                        [["dotnet", "build"]], 300, self.results, "agent-check build", "build",
                    )
                    self.assertEqual(run.call_args.args[2], 1000 + expected)
                    self.assertIn(f"TIMEOUT after {expected}s", summary)

    def test_identical_checks_wait_for_one_result_and_a_later_call_runs_again(self):
        owner_started = threading.Event()
        follower_waiting = threading.Event()
        release_owner = threading.Event()
        key = uuid.uuid4().hex
        original_flock = agent_check.fcntl.flock

        def observe_wait(file, operation):
            if operation == agent_check.fcntl.LOCK_SH:
                follower_waiting.set()
            return original_flock(file, operation)

        def run_check(*_args):
            owner_started.set()
            if not release_owner.wait(10):
                raise TimeoutError("The test did not release the first check")
            return 1, "shared failed result"

        def invocation():
            return tuple(agent_check.shared_result(key, [], 1, self.results, "agent-check build", "build"))

        with (
            patch.object(agent_check.fcntl, "flock", side_effect=observe_wait),
            patch.object(agent_check, "run_check", side_effect=run_check) as run,
            ThreadPoolExecutor(max_workers=2) as executor,
        ):
            leader = executor.submit(invocation)
            try:
                self.assertTrue(owner_started.wait(5))
                follower = executor.submit(invocation)
                self.assertTrue(follower_waiting.wait(5))
                self.assertFalse(follower.done())
                self.assertEqual(run.call_count, 1)
            finally:
                release_owner.set()
            self.assertEqual(leader.result(5), (1, "shared failed result"))
            self.assertEqual(follower.result(5), leader.result())
            self.assertEqual(run.call_count, 1)
            self.assertEqual(invocation(), leader.result())
            self.assertEqual(run.call_count, 2)

    def test_guards_cover_each_review_plan_dotnet_project(self):
        project_config = (SCRIPT.parent.parent / ".agent-studio/project.yml").read_text()
        test_section = project_config.split("  test:\n", 1)[1].split("  lint:\n", 1)[0]
        projects = re.findall(r"(?m)^    - dotnet test (\S+) --no-build", test_section)
        _, commands = agent_check.check_commands(agent_check.parse_check(["guards"]))
        self.assertEqual([command[2] for command in commands], projects)
        guard_filter = "(Category!=MachineBound)&(FullyQualifiedName~.Architecture.|Category=Guard)"
        self.assertEqual([command[-1] for command in commands], [guard_filter] * len(projects))

    def test_frontend_specs_forward_paths_to_the_existing_scripts(self):
        cases = [
            (["frontend-specs", "src/first.spec.ts", "src/second spec.ts"],
             ["test:ci", "--", "--include", "src/first.spec.ts", "--include", "src/second spec.ts"]),
            (["frontend-e2e", "e2e/example.spec.ts"], ["e2e", "--", "e2e/example.spec.ts"]),
            (["frontend-e2e"], ["e2e"]),
        ]
        for args, npm_args in cases:
            with self.subTest(args=args):
                _, commands = agent_check.check_commands(agent_check.parse_check(args))
                self.assertEqual(commands, [["bash", "scripts/npm-frontend.sh", "run", *npm_args]])


if __name__ == "__main__":
    unittest.main()
