#!/usr/bin/env python3
import datetime as dt
import json
import os
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
STACK = ROOT / 'deploy/compose/monolith-networked'


class EncryptedBackupTests(unittest.TestCase):
    def test_restore_and_monitored_result(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            data = root / 'data'
            for name in ('workspace', 'projects', 'home', 'caddy-data', 'caddy-config',
                         'frontend-data', 'frontend-config', 'origin-pull'):
                (data / name).mkdir(parents=True)
            (data / 'workspace/state.txt').write_text('scratch-state-' + os.urandom(8).hex())
            install = root / 'install'
            install.mkdir()
            (install / '.env').write_text('STUDIO_DOMAIN=restore.example.test\n')
            key, cert = root / 'key.pem', root / 'cert.pem'
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                            '-days', '2', '-subj', '/CN=restore-test', '-keyout', str(key),
                            '-out', str(cert)], check=True, capture_output=True)
            archive = root / 'backup.cms'
            subprocess.run(['bash', str(STACK / 'backup-encrypted.sh'), str(data),
                            str(install), str(cert), str(archive)], check=True, capture_output=True)
            self.assertNotIn((data / 'workspace/state.txt').read_bytes(), archive.read_bytes())
            signal = root / 'restore-signal.json'
            env = os.environ.copy()
            env['STUDIO_RESTORE_PROBE_COMMAND'] = 'test -s "$STUDIO_SCRATCH_DIR/data/workspace/state.txt"'
            restored = subprocess.run(['bash', str(STACK / 'restore-to-scratch.sh'),
                                       str(archive), str(cert), str(key), str(root / 'scratch'),
                                       str(STACK), str(signal)], env=env, capture_output=True, text=True)
            self.assertEqual(0, restored.returncode, restored.stderr)
            self.assertEqual('pass', json.loads(signal.read_text())['status'])
            self.assertEqual((data / 'workspace/state.txt').read_text(),
                             (root / 'scratch/data/workspace/state.txt').read_text())
            wrong_key, wrong_cert = root / 'wrong-key.pem', root / 'wrong-cert.pem'
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                            '-days', '2', '-subj', '/CN=wrong-recipient', '-keyout', str(wrong_key),
                            '-out', str(wrong_cert)], check=True, capture_output=True)
            failed_signal = root / 'failed-restore.json'
            failed = subprocess.run(['bash', str(STACK / 'restore-to-scratch.sh'),
                                     str(archive), str(wrong_cert), str(wrong_key),
                                     str(root / 'failed-scratch'), str(STACK), str(failed_signal)],
                                    env=env, capture_output=True, text=True)
            self.assertNotEqual(0, failed.returncode)
            self.assertEqual('fail', json.loads(failed_signal.read_text())['status'])
            events = root / 'access-events.jsonl'
            when = dt.datetime.now(dt.timezone.utc).isoformat()
            events.write_text(''.join(json.dumps({'type': event_type, 'time': when}) + '\n'
                                      for event_type in ['session-created'] + ['login-failed'] * 5))
            leaf = root / 'leaf.pem'
            leaf.write_bytes(cert.read_bytes())
            edge_cert = data / 'caddy-data/caddy/certificates/test/edge.crt'
            edge_cert.parent.mkdir(parents=True)
            edge_cert.write_bytes(cert.read_bytes())
            monitored = subprocess.run(['python3', str(STACK / 'monitor-baseline.py'),
                                        str(events), str(leaf), str(data / 'caddy-data'), str(signal)],
                                       capture_output=True, text=True)
            self.assertEqual(1, monitored.returncode)
            alerts = json.loads(monitored.stdout)['alerts']
            self.assertIn('new-session', alerts)
            self.assertIn('login-failures', alerts)
            self.assertIn('certificate-expiry', alerts)


if __name__ == '__main__':
    unittest.main()
