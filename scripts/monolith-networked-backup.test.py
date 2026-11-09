#!/usr/bin/env python3
import base64
import datetime as dt
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
STACK = ROOT / 'deploy/compose/monolith-networked'

# Stand-ins for the scratch containers: docker only reports a loopback port, and
# curl answers /api/auth/login the way the restored API does since AGT-3379. An
# Owner without an enrolled factor gets only an enrollment session; an enrolled
# Owner needs the current RFC 6238 code.
FAKE_DOCKER = '''#!/usr/bin/env bash
case " $* " in *" port edge 443 "*) echo 127.0.0.1:49443 ;; esac
'''
FAKE_CURL = '''#!/usr/bin/env python3
import base64, hashlib, hmac, json, os, struct, sys, time
def answer(value):
    print(json.dumps(value, separators=(',', ':')))  # compact like ASP.NET
body = json.load(open(sys.argv[sys.argv.index('--data-binary') + 1][1:]))
secret = os.environ.get('FAKE_ENROLLED_TOTP_SECRET')
def code(step):
    digest = hmac.new(base64.b32decode(secret), struct.pack('>q', step), hashlib.sha1).digest()
    offset = digest[-1] & 15
    return '%06d' % ((struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000)
if body.get('username') != os.environ['FAKE_USER'] or body.get('password') != os.environ['FAKE_PASSWORD']:
    answer({'code': 'invalid-credentials'})
elif not secret:
    answer({'authenticated': True, 'secondFactorEnrollmentRequired': True})
elif body.get('secondFactorCode') in {code(int(time.time()) // 30 + offset) for offset in (-1, 0, 1)}:
    answer({'authenticated': True, 'secondFactorEnrollmentRequired': False})
else:
    answer({'code': 'second-factor-required'})
'''


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

    def test_signin_probe_requires_owner_second_factor(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            data = root / 'data'
            for name in ('workspace', 'projects', 'home', 'caddy-data', 'caddy-config',
                         'frontend-data', 'frontend-config', 'origin-pull'):
                (data / name).mkdir(parents=True)
            install = root / 'install'
            install.mkdir()
            (install / '.env').write_text('STUDIO_EDGE_DIGEST=' + os.urandom(32).hex() + '\n')
            for source in [STACK / 'compose.yaml', STACK / 'cloudflare-ips.caddy', *STACK.glob('Caddyfile.*')]:
                shutil.copy2(source, install / source.name)
            key, cert = root / 'key.pem', root / 'cert.pem'
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                            '-days', '2', '-subj', '/CN=restore-test', '-keyout', str(key),
                            '-out', str(cert)], check=True, capture_output=True)
            archive = root / 'backup.cms'
            subprocess.run(['bash', str(STACK / 'backup-encrypted.sh'), str(data),
                            str(install), str(cert), str(archive)], check=True, capture_output=True)
            tools = root / 'bin'
            tools.mkdir()
            for name, text in (('docker', FAKE_DOCKER), ('curl', FAKE_CURL)):
                (tools / name).write_text(text)
                (tools / name).chmod(0o755)
            credentials = root / 'restore-credentials'
            credentials.mkdir()
            user, password = 'owner-' + os.urandom(4).hex(), os.urandom(12).hex()
            totp_secret = base64.b32encode(os.urandom(20)).decode()
            (credentials / 'user').write_text(user)
            (credentials / 'password').write_text(password)
            (credentials / 'totp').write_text(totp_secret + '\n')
            env = os.environ.copy()
            env.pop('STUDIO_RESTORE_PROBE_COMMAND', None)
            env.update(PATH=f'{tools}:{env["PATH"]}', FAKE_USER=user, FAKE_PASSWORD=password,
                       STUDIO_RESTORE_USER_FILE=str(credentials / 'user'),
                       STUDIO_RESTORE_PASSWORD_FILE=str(credentials / 'password'))

            def restore(name, **extra):
                signal = root / f'{name}.json'
                result = subprocess.run(['bash', str(STACK / 'restore-to-scratch.sh'), str(archive),
                                         str(cert), str(key), str(root / name), str(install), str(signal)],
                                        env={**env, **extra}, capture_output=True, text=True, timeout=120)
                return result, json.loads(signal.read_text())

            # An enrollment-only session is no proof that the restored Owner can work.
            result, signal = restore('unenrolled', STUDIO_RESTORE_TOTP_SECRET_FILE=str(credentials / 'totp'))
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertEqual(('fail', 'signin-enrollment-only'), (signal['status'], signal['reason']))
            env['FAKE_ENROLLED_TOTP_SECRET'] = totp_secret
            result, signal = restore('no-factor')
            self.assertEqual(2, result.returncode)
            self.assertIn('STUDIO_RESTORE_TOTP_SECRET_FILE', result.stderr)
            self.assertEqual('fail', signal['status'])
            (credentials / 'other-totp').write_text(base64.b32encode(os.urandom(20)).decode())
            result, signal = restore('wrong-factor', STUDIO_RESTORE_TOTP_SECRET_FILE=str(credentials / 'other-totp'))
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertEqual(('fail', 'signin-refused'), (signal['status'], signal['reason']))
            result, signal = restore('enrolled', STUDIO_RESTORE_TOTP_SECRET_FILE=str(credentials / 'totp'))
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(('pass', 'restored-and-signed-in'), (signal['status'], signal['reason']))
            self.assertFalse((root / 'enrolled/login.json').exists(), 'credentials stay out of the scratch tree')
            shutil.copy2(credentials / 'totp', install / 'totp')
            result, _ = restore('beside-stack', STUDIO_RESTORE_TOTP_SECRET_FILE=str(install / 'totp'))
            self.assertEqual(2, result.returncode)
            self.assertIn('kept separately', result.stderr)


if __name__ == '__main__':
    unittest.main()
