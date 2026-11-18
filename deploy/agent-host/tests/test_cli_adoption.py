"""Exercise CLI activation against isolated legacy and emergency-install layouts."""
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class CliAdoptionTests(unittest.TestCase):
    def exercise(self, layout, fail_restart=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            local_bin = root / 'local-bin'
            usr_bin = root / 'usr-bin'
            local_bin.mkdir()
            usr_bin.mkdir()
            original = {}
            for name in ('codex', 'claude'):
                old = root / f'old-{name}'
                old.write_text('previous launcher')
                locations = [usr_bin] if layout == 'legacy' else [local_bin, usr_bin]
                for directory in locations:
                    launcher = directory / name
                    launcher.symlink_to(old)
                    original[launcher] = str(old)
            # The operator's emergency prefix is a valid previous launcher target.
            if layout == 'emergency':
                codex = root / 'releases/codex-0.166.0/node_modules/@openai/codex/bin/codex.js'
                codex.parent.mkdir(parents=True)
                codex.write_text('emergency codex')
                (usr_bin / 'codex').unlink()
                (usr_bin / 'codex').symlink_to(codex)
                original[usr_bin / 'codex'] = str(codex)
            npm = root / 'npm'
            npm.write_text('''#!/bin/bash
set -eu
mkdir -p "$3/node_modules/.bin"
for name in codex claude; do
  printf '#!/bin/sh\\nexit 0\\n' >"$3/node_modules/.bin/$name"
  chmod +x "$3/node_modules/.bin/$name"
done
''')
            npm.chmod(0o755)
            source = (ROOT / 'agent-runner-deploy').read_text()
            source = source.replace('-o root -g root ', '')
            source = source.replace('readonly cli_root="/opt/agent-cli"', f'readonly cli_root="{root}/managed"')
            source = source.replace('readonly npm_binary="/usr/bin/npm"', f'readonly npm_binary="{npm}"')
            source = source.replace('"/usr/local/bin/$name"', f'"{local_bin}/$name"')
            source = source.replace('"/usr/bin/$name"', f'"{usr_bin}/$name"')
            helper = root / 'helper'
            helper.write_text(source)
            restart = 'return 1' if fail_restart else 'return 0'
            harness = f'''set -euo pipefail
source {helper}
probe_staged_cli() {{ :; }}
assert_review_restart_is_safe() {{ :; }}
unit_main_pid() {{ echo 42; }}
restart_unit_and_wait_for_new_main_pid() {{ :; }}
best_effort_replace_unit() {{ :; }}
logger() {{ :; }}
chown() {{ :; }}
systemctl() {{ {restart}; }}
update_clis 0.166.0 2.1.305
'''
            result = subprocess.run(['bash', '-c', harness], text=True, capture_output=True)
            self.assertEqual(2 if fail_restart else 0, result.returncode, result.stderr)
            if not fail_restart:
                for launcher in original:
                    self.assertEqual(str(root / f'managed/current/node_modules/.bin/{launcher.name}'), str(launcher.readlink()))
                rollback = next((root / 'managed').glob('rollback-*.sh'))
                self.assertEqual(0o600, rollback.stat().st_mode & 0o777)
                subprocess.run(['bash', str(rollback)], check=True)
            for launcher, target in original.items():
                self.assertEqual(target, str(launcher.readlink()))
            self.assertFalse((root / 'managed/current').is_symlink())

    def test_legacy_usr_bin_launchers_are_adopted_and_rollback_restores_them(self):
        self.exercise('legacy')

    def test_emergency_prefix_and_both_launcher_locations_are_preserved(self):
        self.exercise('emergency')

    def test_failed_restart_restores_exact_previous_launchers(self):
        self.exercise('emergency', fail_restart=True)

    def test_regular_launcher_is_refused_before_install(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'codex').write_text('custom wrapper')
            source = (ROOT / 'agent-runner-deploy').read_text().replace('"/usr/local/bin/$name"', f'"{root}/$name"')
            helper = root / 'helper'
            helper.write_text(source)
            result = subprocess.run(['bash', '-c', f'source {helper}; cli_launchers codex'], capture_output=True, text=True)
            self.assertEqual(2, result.returncode)
            self.assertIn('must be a symbolic link', result.stderr)
            self.assertEqual('custom wrapper', (root / 'codex').read_text())
