import os
import hashlib
import json
import shutil
import subprocess
import tempfile
import textwrap
import unittest
import uuid
from itertools import product
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
    def test_runbook_never_approves_failed_publish_or_digest_output(self):
        runbook = (HELPER_PATH.parents[2] / "docs/operations/setup/linux-runner-host.md").read_text()
        commands = runbook.split("## 2. Build agent-host", 1)[1].split("```bash\n", 1)[1].split("```", 1)[0]
        commands = commands.replace("<origin>", "fixture-origin")
        steps = [
            "clone", "publish", "harden", "digest", "install-directory",
            "install-approval", "inspect-incoming", "upload", "promote",
        ]
        for conditional, failure in product((False, True), (*steps, "none")):
            with self.subTest(conditional=conditional, failure=failure), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                (root / "agent-taskboard").mkdir()
                log = root / "calls"
                fixture = '''
                    record() {
                      printf '%s\\n' "$1" >>"$CALL_LOG"
                      [[ "$1" != "$FAIL_STEP" ]] || return 19
                    }
                    git() {
                      case "$1" in
                        clone) record clone ;;
                        rev-parse) printf '%s\\n' fixture-revision ;;
                        *) return 90 ;;
                      esac
                    }
                    dotnet() {
                      # A failed publish may leave an executable in its output.
                      printf '%s\\n' partial-build >"${@: -1}/agent-host"
                      record publish
                    }
                    sudo() {
                      case "$1" in
                        ./scripts/harden-agent-runner-host.sh) record harden ;;
                        /usr/local/sbin/agent-runner-deploy)
                          if [[ "${2:-}" == --release-digest ]]; then
                            printf '%s\\n' fixture-digest
                            record digest
                          else
                            record promote
                          fi ;;
                        install)
                          if [[ "$2" == -d ]]; then record install-directory
                          else record install-approval; fi ;;
                        find) record inspect-incoming ;;
                        -u) cat >/dev/null ;;
                        *) return 90 ;;
                      esac
                    }
                    tar() {
                      printf '%s\\n' fixture-archive
                      record upload
                    }
                '''
                # Bash suppresses errexit inside commands tested by a conditional.
                script = f'if (\n{commands}\n); then exit 0; else exit "$?"; fi\n' if conditional else commands
                result = subprocess.run(
                    ["bash", "--noprofile", "--norc", "-c", textwrap.dedent(fixture) + script],
                    cwd=root,
                    env={**os.environ, "CALL_LOG": str(log), "FAIL_STEP": failure,
                         "TMPDIR": temporary, "SHELLOPTS": ""},
                    capture_output=True, text=True,
                )
                expected_steps = steps if failure == "none" else steps[:steps.index(failure) + 1]
                self.assertEqual(expected_steps, log.read_text().splitlines(), result.stderr)
                self.assertEqual(0 if failure == "none" else 19, result.returncode, result.stderr)

    def test_runbook_migration_stops_at_each_failed_verification_or_install(self):
        runbook = (HELPER_PATH.parents[2] / "docs/operations/setup/linux-runner-host.md").read_text()
        migration = runbook.split("### Release approval migration", 1)[1]
        commands = migration.split("```bash\n")[2].split("```", 1)[0]
        commands = commands.replace("<rollback-id>", "fixture-rollback")
        steps = ["resolve", "verify-active", "verify-rollback", "harden", "verify-installed"]
        for conditional, failure in product((False, True), (*steps, "none")):
            with self.subTest(conditional=conditional, failure=failure), tempfile.TemporaryDirectory() as temporary:
                log = Path(temporary) / "calls"
                # Execute the documented block in a shell without errexit.
                # These command boundaries never touch a live host or run sudo.
                fixture = '''
                    record() {
                      printf '%s\\n' "$1" >>"$CALL_LOG"
                      [[ "$1" != "$FAIL_STEP" ]] || return 19
                    }
                    readlink() {
                      record resolve || return $?
                      printf '%s\\n' /opt/agent-host/releases/fixture-active
                    }
                    sudo() {
                      case "$*" in
                        'bash ./deploy/agent-host/agent-runner-deploy --verify-release /opt/agent-host/releases/fixture-active')
                          record verify-active ;;
                        'bash ./deploy/agent-host/agent-runner-deploy --verify-release /opt/agent-host/releases/fixture-rollback')
                          record verify-rollback ;;
                        './scripts/harden-agent-runner-host.sh --apply') record harden ;;
                        '/usr/local/sbin/agent-runner-deploy --verify-release /opt/agent-host/releases/fixture-active')
                          record verify-installed ;;
                        *) return 90 ;;
                      esac
                    }
                '''
                script = f'if (\n{commands}\n); then exit 0; else exit "$?"; fi\n' if conditional else commands
                result = subprocess.run(
                    ["bash", "--noprofile", "--norc", "-c", textwrap.dedent(fixture) + script],
                    env={**os.environ, "CALL_LOG": str(log), "FAIL_STEP": failure, "SHELLOPTS": ""},
                    capture_output=True, text=True,
                )
                expected_steps = steps if failure == "none" else steps[:steps.index(failure) + 1]
                self.assertEqual(expected_steps, log.read_text().splitlines(), result.stderr)
                self.assertEqual(0 if failure == "none" else 19, result.returncode, result.stderr)

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


class ReleaseProvenanceTests(unittest.TestCase):
    """Disposable host layout; the unprivileged run substitutes only the trust UID."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.release_id = "fixture-release"
        self.releases = self.root / "product/releases"
        self.release = self.releases / self.release_id
        self.release.mkdir(parents=True)
        self.approvals = self.root / "approvals"
        self.approvals.mkdir()
        self.log = self.root / "calls"
        self.log.touch()
        self.binary = self.release / "agent-host"
        self.binary.write_text(
            '#!/bin/bash\n'
            f'printf "%s %s\\n" "$(id -u)" "$*" >>"{self.log}"\n'
        )
        self.binary.chmod(0o755)
        (self.release / "agent-host.dll").write_bytes(b"fixture assembly")
        (self.release / "empty-directory").mkdir()
        self.current = self.releases.parent / "current"
        self.current.symlink_to(self.release)
        self.helper = self.root / "agent-runner-deploy"
        source = HELPER_PATH.read_text()
        source = source.replace('readonly product_root="/opt/agent-host"',
                                f'readonly product_root="{self.releases.parent}"')
        source = source.replace('readonly release_approvals_root="/etc/agent-host/release-approvals"',
                                f'readonly release_approvals_root="{self.approvals}"')
        # /tmp and the fixture tree are deliberately not a production root hierarchy.
        # Keep the real verifier's path checks inside this isolated host root.
        source = source.replace('path != Path("/")', f'path != Path("{self.root.parent}")')
        source = source.replace('entry.st_uid != 0', f'entry.st_uid != {os.getuid()}')
        self.helper.write_text(source)
        self.helper.chmod(0o755)

    def run_helper(self, body):
        return subprocess.run(["bash", "-c", f'set -euo pipefail; source "{self.helper}"; {body}'],
                              capture_output=True, text=True)

    def approve(self):
        # Independent implementation of the publisher's canonical tree format.
        entries = []
        for path in sorted(self.release.rglob("*"), key=lambda path: path.relative_to(self.release).as_posix()):
            name = path.relative_to(self.release).as_posix()
            entries.append([name, "directory"] if path.is_dir() else
                           [name, "file", hashlib.sha256(path.read_bytes()).hexdigest()])
        digest = hashlib.sha256(json.dumps(entries, ensure_ascii=True, separators=(",", ":")).encode()).hexdigest()
        (self.approvals / f"{self.release_id}.sha256").write_text(digest + "\n")
        return digest

    def verify(self):
        return self.run_helper(f'verify_release_provenance "{self.release}" "{self.release_id}"')

    def test_approved_complete_tree_is_admitted(self):
        self.approve()
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_unapproved_release_is_refused(self):
        result = self.verify()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("release approval", result.stderr)

    def test_changed_binary_dependency_and_added_file_are_refused(self):
        for name in ("agent-host", "agent-host.dll", "extra.runtimeconfig.json"):
            with self.subTest(name=name):
                self.approve()
                path = self.release / name
                previous = path.read_bytes() if path.exists() else None
                path.write_bytes(b"changed release bytes")
                result = self.verify()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("release digest", result.stderr)
                if previous is None:
                    path.unlink()
                else:
                    path.write_bytes(previous)

    def test_writable_or_linked_approval_is_refused(self):
        self.approve()
        approval = self.approvals / f"{self.release_id}.sha256"
        approval.chmod(0o666)
        self.assertNotEqual(0, self.verify().returncode)
        approval.chmod(0o644)
        other = self.root / "approval"
        approval.rename(other)
        approval.symlink_to(other)
        self.assertNotEqual(0, self.verify().returncode)

    def test_link_hardlink_special_file_and_writable_ancestor_are_refused(self):
        for kind in ("symlink", "hardlink", "fifo", "writable-ancestor"):
            with self.subTest(kind=kind):
                self.approve()
                extra = self.release / "extra"
                if kind == "symlink":
                    extra.symlink_to(self.binary)
                elif kind == "hardlink":
                    os.link(self.binary, extra)
                elif kind == "fifo":
                    os.mkfifo(extra)
                else:
                    self.releases.chmod(0o777)
                result = self.verify()
                self.assertNotEqual(0, result.returncode)
                if extra.exists():
                    extra.unlink()
                self.releases.chmod(0o755)

    def test_guard_uses_installed_binary_through_service_identity_and_keeps_busy_refusal(self):
        self.approve()
        staged = self.root / "staged-agent-host"
        staged.write_text('#!/bin/bash\nprintf staged >>"' + str(self.log) + '"\n')
        staged.chmod(0o755)
        result = self.run_helper(f'''systemctl() {{ return 0; }}
            unit_main_pid() {{ echo 123; }}
            review_state_dir() {{ echo "{self.root}/state"; }}
            runuser() {{
              [[ "$1 $2 $3 $4" == "-u agent -- env" ]] || return 90
              shift 3
              "$@"
            }}
            assert_review_restart_is_safe "{staged}"
        ''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("staged", self.log.read_text())
        self.assertIn(f"{os.getuid()} --restart-guard --hold-admission --role review", self.log.read_text())
        self.binary.write_text("#!/bin/bash\nexit 3\n")
        self.approve()
        refused = self.run_helper('''systemctl() { return 0; }
            unit_main_pid() { echo 123; }
            review_state_dir() { echo /fixture/state; }
            runuser() { shift 3; "$@"; }
            assert_review_restart_is_safe
        ''')
        self.assertEqual(2, refused.returncode)
        self.assertIn("slots are busy", refused.stderr)

    def test_drain_uses_the_service_identity_and_retains_stop_confirmation(self):
        result = self.run_helper('''systemctl() { return 0; }
            unit_main_pid() { echo 123; }
            review_state_dir() { echo /fixture/state; }
            logger() { :; }
            wait_for_unit_inactive() { return 0; }
            runuser() {
              [[ "$1 $2 $3 $4" == "-u agent -- env" ]] || return 90
              shift 3; "$@"
            }
            drain_review_unit
        ''')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("--drain --role review --state-dir /fixture/state", self.log.read_text())
        self.assertIn("drained and stopped", result.stdout)
        refused = self.run_helper('''systemctl() { return 0; }
            unit_main_pid() { echo 123; }
            review_state_dir() { echo /fixture/state; }
            logger() { :; }
            runuser() { return 9; }
            wait_for_unit_inactive() { echo unexpected-stop; }
            drain_review_unit
        ''')
        self.assertEqual(2, refused.returncode)
        self.assertIn("drain did not complete (exit 9)", refused.stderr)
        self.assertNotIn("unexpected-stop", refused.stdout)

    def test_launcher_refuses_unapproved_or_changed_release_before_execution(self):
        launcher = self.root / "agent-work-launcher"
        source = (HELPER_PATH.parent / "agent-work-launcher").read_text()
        config = self.root / "launcher.env"
        config.write_text("RUNTIME=docker\n")
        source = source.replace('readonly config="/etc/agent-host/work-container-launcher.env"', f'readonly config="{config}"')
        source = source.replace('readonly product_root="/opt/agent-host"', f'readonly product_root="{self.releases.parent}"')
        source = source.replace('readonly deploy_helper="/usr/local/sbin/agent-runner-deploy"', f'readonly deploy_helper="{self.helper}"')
        # Root admission and ownership are covered separately; this launches only
        # disposable executables as the current unprivileged test identity.
        source = source.replace('(( EUID == 0 ))', 'true')
        source = source.replace('"$path" != /', f'"$path" != "{self.root.parent}"')
        source = source.replace('[[ "$owner" == 0 ]]', f'[[ "$owner" == {os.getuid()} ]]')
        self.helper.write_text(self.helper.read_text().replace('[[ "$EUID" -eq 0 ]]', 'true'))
        launcher.write_text(source)
        def launch(*arguments):
            return subprocess.run(["bash", str(launcher), *(arguments or ("--stdio",))], capture_output=True, text=True)
        self.assertNotEqual(0, launch().returncode)
        self.assertEqual("", self.log.read_text())
        self.approve()
        admitted = launch()
        self.assertEqual(0, admitted.returncode, admitted.stderr)
        self.assertIn("work-container launch --stdio", self.log.read_text())
        self.assertEqual(0, launch("--probe", "agent-work-fixture").returncode)
        self.assertIn("work-container launch inspect --format {{json .Mounts}} agent-work-fixture", self.log.read_text())
        self.log.write_text("")
        (self.release / "agent-host.dll").write_text("tampered")
        self.assertNotEqual(0, launch().returncode)
        self.assertNotEqual(0, launch("--probe", "agent-work-fixture").returncode)
        self.assertEqual("", self.log.read_text())

    @unittest.skipIf(os.geteuid() == 0, "unprivileged probe fixture records a non-root UID")
    def test_staged_smoke_and_installed_guard_record_non_root_uids(self):
        staged = self.root / "staging"
        staged.mkdir()
        shutil.copy2(self.binary, staged / "agent-host")
        fake_bin = self.root / "bin"
        fake_bin.mkdir()
        runuser = fake_bin / "runuser"
        runuser.write_text(
            '#!/bin/bash\nset -euo pipefail\n'
            '[[ "$1 $2 $3" == "-u agent --" ]] || exit 90\n'
            'shift 3\nexec "$@"\n'
        )
        runuser.chmod(0o755)
        # Execute the production probe block, including timeout and runuser.
        # This runner cannot acquire root; the stand-in checks the exact drop
        # arguments and then executes both binaries at our real non-root UID.
        source = HELPER_PATH.read_text()
        promotion = source[source.index("promote_release() {"):]
        block = promotion[promotion.index("  local smoke_output"):
                          promotion.index('  mv -- "$staging_root" "$release_root"')]
        result = self.run_helper(f'''export PATH="{fake_bin}:$PATH"
            systemctl() {{ return 0; }}
            unit_main_pid() {{ echo 123; }}
            review_state_dir() {{ echo "{self.root}/state"; }}
            probes() {{
              local staging_root="{staged}" dotnet_root="{self.root}/dotnet"
              {block}
            }}
            probes
        ''')
        self.assertEqual(0, result.returncode, result.stderr)
        calls = self.log.read_text().splitlines()
        self.assertEqual(2, len(calls), calls)
        self.assertEqual(f"{os.getuid()} --version", calls[0])
        self.assertTrue(calls[1].startswith(f"{os.getuid()} --restart-guard --hold-admission"), calls)
        self.assertTrue(all(not call.startswith("0 ") for call in calls), calls)

    @unittest.skipUnless(os.geteuid() == 0 and shutil.which("useradd") and shutil.which("runuser"),
                         "requires a disposable local account and root without NoNewPrivileges")
    def test_real_root_probes_drop_to_a_disposable_service_account(self):
        account = "agt4106-" + uuid.uuid4().hex[:12]
        subprocess.run(["useradd", "--no-create-home", "--no-log-init", "--user-group", account], check=True)
        self.addCleanup(subprocess.run, ["userdel", account], check=True)
        self.root.chmod(0o755)
        self.log.chmod(0o666)
        self.helper.write_text(self.helper.read_text().replace(
            'readonly service_user="agent"', f'readonly service_user="{account}"'))
        staged = self.root / "staging"
        staged.mkdir()
        shutil.copy2(self.binary, staged / "agent-host")
        source = HELPER_PATH.read_text()
        promotion = source[source.index("promote_release() {"):]
        block = promotion[promotion.index("  local smoke_output"):
                          promotion.index('  mv -- "$staging_root" "$release_root"')]
        result = self.run_helper(f'''systemctl() {{ return 0; }}
            unit_main_pid() {{ echo 123; }}
            review_state_dir() {{ echo "{self.root}/state"; }}
            probes() {{
              local staging_root="{staged}" dotnet_root="{self.root}/dotnet"
              {block}
            }}
            probes
        ''')
        self.assertEqual(0, result.returncode, result.stderr)
        service_uid = subprocess.check_output(["id", "-u", account], text=True).strip()
        self.assertNotEqual("0", service_uid)
        calls = self.log.read_text().splitlines()
        self.assertEqual(2, len(calls), calls)
        self.assertEqual(f"{service_uid} --version", calls[0])
        self.assertTrue(calls[1].startswith(f"{service_uid} --restart-guard --hold-admission"), calls)

    def test_publisher_digest_matches_independent_encoding_and_binds_release_id(self):
        expected = self.approve()
        result = self.run_helper(f'release_provenance "{self.release}" "" digest')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(expected, result.stdout.strip())
        (self.release / "release-id").write_text(self.release_id + "\n")
        self.assertNotEqual(0, self.verify().returncode)
        self.approve()
        self.assertEqual(0, self.verify().returncode)
        removed = self.release / "agent-host.dll"
        removed.unlink()
        self.assertNotEqual(0, self.verify().returncode)
        self.assertNotEqual(0, self.run_helper(
            f'verify_release_provenance "{self.release}" other-release').returncode)

    def test_runbook_legacy_approval_matches_the_promoted_layout(self):
        runbook = (HELPER_PATH.parents[2] / "docs/operations/setup/linux-runner-host.md").read_text()
        migration = runbook.split("### Release approval migration", 1)[1]
        commands = migration.split("```bash\n", 1)[1].split("```", 1)[0]
        trusted_publish = self.root / "trusted-publish"
        shutil.copytree(self.release, trusted_publish)
        # Legacy promotion selected the directory name, then removed release-id.
        # Publisher archives may contain the marker or just dotnet publish output.
        checkout = self.root / "checkout"
        helper = checkout / "deploy/agent-host/agent-runner-deploy"
        helper.parent.mkdir(parents=True)
        helper.write_text(self.helper.read_text().replace('[[ "$EUID" -eq 0 ]]', 'true'))
        commands = commands.replace("/absolute/trusted/publish", str(trusted_publish))
        verify = ["bash", str(helper), "--verify-release", str(self.release)]
        for has_marker in (False, True):
            with self.subTest(publisher_has_release_id=has_marker):
                marker = trusted_publish / "release-id"
                if has_marker:
                    marker.write_text(self.release_id + "\n")
                result = subprocess.run(
                    ["bash", "-c", 'set -euo pipefail; sudo() { [[ "$1" == bash ]] || return 90; "$@"; };\n' + commands],
                    cwd=checkout, capture_output=True, text=True,
                )
                self.assertEqual(0, result.returncode, result.stderr)
                (self.approvals / f"{self.release_id}.sha256").write_text(result.stdout.strip() + "\n")
                verification = subprocess.run(verify, capture_output=True, text=True)
                self.assertEqual(0, verification.returncode, verification.stderr)
                self.assertEqual(has_marker, marker.exists())
                if has_marker:
                    self.assertEqual(self.release_id + "\n", marker.read_text())
        self.assertFalse((self.release / "release-id").exists())
        # Layout conversion must not bless unexplained host content changes.
        (self.release / "agent-host.dll").write_bytes(b"changed legacy assembly")
        verification = subprocess.run(verify, capture_output=True, text=True)
        self.assertNotEqual(0, verification.returncode)
        self.assertIn("release digest does not match the publisher approval", verification.stderr)
        self.assertEqual("", self.log.read_text())

    def test_approval_ownership_and_directory_ownership_are_enforced(self):
        self.approve()
        # Model another UID without requiring privilege to chown fixture files.
        self.helper.write_text(self.helper.read_text().replace(
            f'entry.st_uid != {os.getuid()}', f'entry.st_uid != {os.getuid() + 1}'))
        self.assertNotEqual(0, self.verify().returncode)

    def test_guard_still_fails_closed_on_control_error_and_pid_change(self):
        for failure in ("control-error", "pid-change"):
            with self.subTest(failure=failure):
                counter = self.root / "pids"
                counter.write_text("0")
                result = self.run_helper(f'''systemctl() {{ return 0; }}
                    unit_main_pid() {{
                      count="$(cat "{counter}")"
                      echo "$((count + 1))" >"{counter}"
                      if [[ "{failure}" == pid-change && "$count" -gt 1 ]]; then
                        echo 456
                      else
                        echo 123
                      fi
                    }}
                    review_state_dir() {{ echo /fixture/state; }}
                    runuser() {{
                      [[ "{failure}" != control-error ]] || return 9
                      shift 3; "$@"
                    }}
                    assert_review_restart_is_safe
                ''')
                self.assertEqual(2, result.returncode)
                self.assertIn("exit 9" if failure == "control-error" else "MainPID changed", result.stderr)

    def test_promotion_rejects_missing_or_changed_approval_before_any_candidate_call(self):
        validator = self.root / "validate-closure"
        validator.write_text("#!/bin/sh\nexit 0\n")
        validator.chmod(0o755)
        self.helper.write_text(self.helper.read_text().replace(
            'readonly deps_closure_validator="/usr/local/libexec/agent-runner-deps-closure"',
            f'readonly deps_closure_validator="{validator}"'))
        fake_bin = self.root / "bin"
        fake_bin.mkdir()
        runuser = fake_bin / "runuser"
        runuser.write_text('#!/bin/bash\n[[ "$1 $2 $3" == "-u agent --" ]] || exit 90\nshift 3\nexec "$@"\n')
        runuser.chmod(0o755)
        source = HELPER_PATH.read_text()
        promotion = source[source.index("promote_release() {"):]
        block = promotion[promotion.index('  verify_release_provenance "$staging_root"'):
                          promotion.index('  mv -- "$staging_root" "$release_root"')]
        staged = self.releases / ".staging.fixture"
        shutil.copytree(self.release, staged)
        for scenario in ("missing", "changed", "approved"):
            with self.subTest(scenario=scenario):
                if scenario != "missing":
                    self.approve()
                (staged / "agent-host.dll").write_bytes(
                    b"tampered" if scenario == "changed" else b"fixture assembly")
                self.log.write_text("")
                result = self.run_helper(f'''export PATH="{fake_bin}:$PATH"
                    systemctl() {{ return 0; }}
                    unit_main_pid() {{ echo 123; }}
                    review_state_dir() {{ echo /fixture/state; }}
                    check_candidate() {{
                      local staging_root="{staged}" release_id="{self.release_id}" dotnet_root="{self.root}/dotnet"
                      {block}
                    }}
                    check_candidate
                ''')
                if scenario == "approved":
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual(2, len(self.log.read_text().splitlines()))
                else:
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("trusted publisher approval", result.stderr)
                    self.assertEqual("", self.log.read_text())
                self.assertEqual(self.release, self.current.resolve())

    def test_copied_symlink_is_refused_before_any_permission_change(self):
        staged = self.releases / ".staging.fixture"
        staged.mkdir()
        outside = self.root / "outside"
        outside.write_bytes(b"unrelated fixture bytes")
        outside.chmod(0o600)
        (staged / "agent-host").symlink_to(outside)
        source = HELPER_PATH.read_text()
        promotion = source[source.index("promote_release() {"):]
        block = promotion[promotion.index("  # Incoming can change during the copy."):
                          promotion.index("  local closure_output")]
        result = self.run_helper(f'''check_snapshot() {{
              local staging_root="{staged}" release_id="{self.release_id}" invalid_entry
              chown() {{ echo unexpected-chown; }}
              {block}
            }}
            check_snapshot
        ''')
        self.assertEqual(2, result.returncode)
        self.assertIn("snapshot contains a non-regular entry", result.stderr)
        self.assertNotIn("unexpected-chown", result.stdout)
        self.assertEqual(0o600, outside.stat().st_mode & 0o777)

    @unittest.skipIf(os.geteuid() == 0, "requires a service identity")
    def test_service_identity_cannot_call_privileged_helper_or_launcher_protocols(self):
        commands = (
            [str(HELPER_PATH), "--verify-release", str(self.release)],
            [str(HELPER_PATH), "--release-digest", str(self.release)],
            [str(HELPER_PATH.parent / "agent-work-launcher"), "--stdio"],
            [str(HELPER_PATH.parent / "agent-work-launcher"), "--probe", "agent-work-fixture"],
        )
        for command in commands:
            with self.subTest(command=command[1:]):
                result = subprocess.run(["bash", *command], capture_output=True, text=True)
                self.assertEqual(2, result.returncode, result.stderr)
                self.assertIn("root", result.stderr)
        self.assertEqual("", self.log.read_text())

    def test_snapshot_stays_private_until_normalized_and_authenticated(self):
        source = HELPER_PATH.read_text()
        promotion = source[source.index("promote_release() {"):]
        self.assertIn('install -d -o root -g root -m 0700 "$staging_root"', promotion)
        self.assertIn('(umask 077; cp -R --no-preserve=all', promotion)
        self.assertIn('find "$staging_root" -mindepth 1 -type d -exec chmod 0755', promotion)
        seal = promotion.index('verify_release_provenance "$staging_root" "$release_id"')
        opening = promotion.index('chmod 0755 "$staging_root"')
        self.assertLess(promotion.index('-type f -exec chmod 0644'), seal)
        self.assertLess(seal, opening)
        self.assertLess(opening, promotion.index('./agent-host --version'))

    def test_promotion_authenticates_the_snapshot_before_smoke_and_uses_installed_guard(self):
        helper = HELPER_PATH.read_text()
        start = helper.index("promote_release() {")
        promotion = helper[start:helper.index("main() {", start)]
        verification = promotion.index('verify_release_provenance "$staging_root" "$release_id"')
        self.assertLess(verification, promotion.index('./agent-host --version'))
        self.assertIn('runuser -u "$service_user"', promotion)
        self.assertIn('assert_review_restart_is_safe "$(installed_agent_host_binary)"', promotion)


if __name__ == "__main__":
    unittest.main()
