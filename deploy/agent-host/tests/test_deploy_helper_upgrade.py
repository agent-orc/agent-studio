"""The operator bootstrap replaces old helpers without expanding agent sudo."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class DeployHelperUpgradeTests(unittest.TestCase):
    def exercise(self, reject_final_policy=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            checkout = root / 'release'
            (checkout / 'scripts').mkdir(parents=True)
            shutil.copytree(ROOT / 'deploy/agent-host', checkout / 'deploy/agent-host')
            host = root / 'host'
            installed = host / 'usr/local/sbin/agent-runner-deploy'
            installed.parent.mkdir(parents=True)
            installed.write_text('old helper without update-clis')
            policy = host / 'etc/sudoers.d/agent-runner'
            policy.parent.mkdir(parents=True)
            policy.write_text('old sudo policy')
            fake_bin = root / 'bin'
            fake_bin.mkdir()
            visudo = fake_bin / 'visudo'
            visudo.write_text('#!/bin/sh\n' + ('[ "$1" != -c ]\n' if reject_final_policy else 'exit 0\n'))
            visudo.chmod(0o755)
            source = (ROOT / 'scripts/upgrade-agent-runner-deploy.sh').read_text()
            source = '\n'.join(line for line in source.splitlines() if not line.startswith('[[ "$EUID"'))
            source = source.replace('readonly destination_root=""', f'readonly destination_root="{host}"')
            source = source.replace('-o root -g root ', '')
            script = checkout / 'scripts/upgrade-agent-runner-deploy.sh'
            script.write_text(source)
            result = subprocess.run(['bash', str(script), '--apply'], text=True, capture_output=True,
                                    env={**os.environ, 'PATH': f'{fake_bin}:' + os.environ['PATH']})
            if reject_final_policy:
                self.assertNotEqual(0, result.returncode)
                self.assertEqual('old helper without update-clis', installed.read_text())
                self.assertEqual('old sudo policy', policy.read_text())
                self.assertFalse((host / 'usr/local/libexec/agent-runner-config-policy').exists())
            else:
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn('update-clis', installed.read_text())
                self.assertIn('update-clis 0.166.0 2.1.305', policy.read_text())
                self.assertEqual(0o440, policy.stat().st_mode & 0o777)
                rollback = next((host / 'var/backups/agent-runner-deploy').glob('*/rollback.sh'))
                self.assertEqual(0o600, rollback.stat().st_mode & 0o777)
                subprocess.run(['bash', str(rollback)], check=True)
                self.assertEqual('old helper without update-clis', installed.read_text())
                self.assertEqual('old sudo policy', policy.read_text())

    def test_old_helper_and_sudoers_are_upgraded_together_with_backup(self):
        self.exercise()

    def test_invalid_combined_policy_restores_previous_files(self):
        self.exercise(reject_final_policy=True)
