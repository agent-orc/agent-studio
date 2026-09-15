import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


HELPER_PATH = Path(__file__).resolve().parents[1] / "agent-runner-deploy"


class ConfigVerificationTests(unittest.TestCase):
    def run_verification_fixture(
        self, scenario: str
    ) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as temporary_directory:
            fixture_root = Path(temporary_directory)
            proc_root = fixture_root / "proc"
            fake_bin = fixture_root / "bin"
            proc_root.mkdir()
            fake_bin.mkdir()

            patched_helper = fixture_root / "agent-runner-deploy"
            helper = HELPER_PATH.read_text(encoding="utf-8")
            helper = helper.replace(
                'readonly proc_root="/proc"',
                f'readonly proc_root="{proc_root}"',
            )
            patched_helper.write_text(helper, encoding="utf-8")

            old_pid = "101"
            new_pid = "202"
            new_value = "3" if scenario == "handoff" else "2"
            for pid, value in ((old_pid, "2"), (new_pid, new_value)):
                environment_path = proc_root / pid / "environ"
                environment_path.parent.mkdir()
                environment_path.write_bytes(
                    f"RUNNER_MAX_PARALLELISM={value}\0".encode("utf-8")
                )

            systemctl = fake_bin / "systemctl"
            systemctl.write_text(
                textwrap.dedent(
                    f"""\
                    #!/usr/bin/env bash
                    set -euo pipefail
                    count_file={fixture_root / 'systemctl-count'}
                    count=0
                    [[ ! -f "$count_file" ]] || count="$(<"$count_file")"
                    count=$((count + 1))
                    printf '%s\n' "$count" >"$count_file"
                    [[ "$1" == show ]]
                    if [[ "{scenario}" == handoff && "$count" -eq 1 ]]; then
                      printf 'ActiveState=active\nMainPID={old_pid}\n'
                    else
                      printf 'ActiveState=active\nMainPID={new_pid}\n'
                    fi
                    """
                ),
                encoding="utf-8",
            )
            systemctl.chmod(0o755)

            harness = fixture_root / "verify.sh"
            harness.write_text(
                textwrap.dedent(
                    f"""\
                    #!/usr/bin/env bash
                    set -euo pipefail
                    source {patched_helper}
                    export PATH={fake_bin}:$PATH
                    sleep() {{
                      if [[ {scenario} == reexec ]]; then
                        printf 'RUNNER_MAX_PARALLELISM=3\\0' >{proc_root / new_pid / 'environ'}
                      fi
                      SECONDS=$((SECONDS + 1))
                    }}
                    wait_for_unit_effective_environment_value \\
                      agent-runner-review.service {old_pid} \\
                      RUNNER_MAX_PARALLELISM 3 2
                    """
                ),
                encoding="utf-8",
            )
            harness.chmod(0o755)

            return subprocess.run(
                ["bash", str(harness)],
                check=False,
                capture_output=True,
                text=True,
                env={**os.environ, "PATH": f"{fake_bin}:{os.environ['PATH']}"},
            )

    def test_waits_past_the_old_main_pid_and_reads_the_new_unit_environment(self):
        result = self.run_verification_fixture("handoff")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("202\n", result.stdout)

    def test_retries_while_a_new_main_pid_reexecs_into_its_effective_environment(self):
        result = self.run_verification_fixture("reexec")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("202\n", result.stdout)

    def test_a_stable_effective_environment_mismatch_fails_within_the_bound(self):
        result = self.run_verification_fixture("mismatch")

        self.assertEqual(12, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)


class ControlPlanePromotionTests(unittest.TestCase):
    def test_runbook_updates_root_owned_control_assets_before_release_promotion(self):
        repository_root = HELPER_PATH.parents[2]
        runbook = (
            repository_root / "docs/operations/setup/linux-runner-host.md"
        ).read_text(encoding="utf-8")
        hardening = (
            repository_root / "scripts/harden-agent-runner-host.sh"
        ).read_text(encoding="utf-8")

        hardening_step = "sudo ./scripts/harden-agent-runner-host.sh --apply"
        promotion_step = "sudo /usr/local/sbin/agent-runner-deploy"
        source_promotion = runbook[runbook.index("## 2. Build agent-host") :]
        self.assertLess(
            source_promotion.index(hardening_step),
            source_promotion.index(promotion_step),
        )
        self.assertIn("normal incoming directory is writable by the unprivileged", runbook)
        self.assertIn(
            'install -o root -g root -m 0755 "$helper_source" "$installed_helper"',
            hardening,
        )
        self.assertIn(
            'install -o root -g root -m 0755 "$policy_source" "$installed_policy"',
            hardening,
        )
        self.assertIn(
            'install -o root -g root -m 0755 "$deps_validator_source" "$installed_deps_validator"',
            hardening,
        )


if __name__ == "__main__":
    unittest.main()
