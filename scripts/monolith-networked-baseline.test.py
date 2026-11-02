#!/usr/bin/env python3
"""Focused deployment baseline regression checks."""
import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
STACK = ROOT / "deploy/compose/monolith-networked"


class WebsiteBaselineTests(unittest.TestCase):
    def check(self, stack):
        return subprocess.run(
            ["bash", str(stack / "check-edge-config.sh"), "--baseline-only", str(stack)],
            capture_output=True, text=True, check=False,
        )

    def test_reference_and_removed_settings(self):
        with tempfile.TemporaryDirectory() as directory:
            stack = pathlib.Path(directory)
            for name in ("compose.yaml", "Caddyfile.public", "Caddyfile.cloudflare",
                         "Caddyfile.frontend", "check-edge-config.sh", "backup-encrypted.sh",
                         "restore-to-scratch.sh", "monitor-baseline.py"):
                shutil.copy2(STACK / name, stack / name)
            result = self.check(stack)
            self.assertEqual(0, result.returncode, result.stderr)
            path = stack / 'compose.yaml'
            original = path.read_text()
            for service in ('api', 'frontend', 'edge'):
                head, rest = original.split(f'  {service}:\n', 1)
                body, tail = re.split(r'(?=^  [a-z-]+:\n|^networks:\n)', rest,
                                      maxsplit=1, flags=re.M)
                mount_marker = ':/data/workspace' if service == 'api' else ':/data'
                for marker, reason in (
                    ('read_only: true', 'read-only'),
                    ('cap_drop:', 'cap_drop'),
                    ('no-new-privileges:true', 'no-new-privileges'),
                    ("user: '10001:10001'", 'non-root'),
                    ('@sha256:', 'digest'),
                    ('tmpfs: [/tmp]', 'writable tmpfs'),
                    (mount_marker, 'writable volume'),
                ):
                    self.assertIn(marker, body)
                    path.write_text(head + f'  {service}:\n' + body.replace(marker, '', 1) + tail)
                    result = self.check(stack)
                    self.assertNotEqual(0, result.returncode, (service, marker))
                    self.assertIn(reason, result.stderr, (service, marker))
                path.write_text(original)
            for name in ('Caddyfile.public', 'Caddyfile.cloudflare'):
                path = stack / name
                original = path.read_text()
                path.write_text(original.replace('forward_auth', '', 1))
                result = self.check(stack)
                self.assertNotEqual(0, result.returncode, name)
                self.assertIn('access layer', result.stderr, name)
                path.write_text(original)
            for name, marker, reason in (
                ('backup-encrypted.sh', '-aes-256-gcm', 'encrypted backup'),
                ('restore-to-scratch.sh', 'website.restore.signin', 'monitored scratch restore'),
                ('monitor-baseline.py', 'login-failures', 'login failure alert'),
                ('monitor-baseline.py', 'new-session', 'new session alert'),
                ('monitor-baseline.py', 'certificate-expiry', 'certificate expiry alert'),
            ):
                path = stack / name
                original = path.read_text()
                path.write_text(original.replace(marker, '', 1))
                result = self.check(stack)
                self.assertNotEqual(0, result.returncode, name)
                self.assertIn(reason, result.stderr, name)
                path.write_text(original)


if __name__ == "__main__":
    unittest.main()
