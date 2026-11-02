#!/usr/bin/env python3
"""Emit owner-facing alerts from access events, certificate and restore proof."""
import datetime as dt
import json
import pathlib
import subprocess
import sys


def main() -> int:
    if len(sys.argv) != 5:
        print('Usage: monitor-baseline.py <access-events.jsonl> <origin-leaf.pem> <edge-caddy-data> <restore-signal.json>', file=sys.stderr)
        return 2
    events, certificate, edge_data, restore_signal = map(pathlib.Path, sys.argv[1:])
    now = dt.datetime.now(dt.timezone.utc)
    alerts = []
    failures = 0
    if not events.is_file():
        alerts.append('access-events-missing')
    else:
        for line in events.read_text().splitlines():
            if not line.strip():
                continue
            event = json.loads(line)
            when = dt.datetime.fromisoformat(event['time'].replace('Z', '+00:00'))
            if now - when > dt.timedelta(minutes=15) or when > now:
                continue
            if event.get('type') == 'login-failed':
                failures += 1
            elif event.get('type') == 'session-created':
                alerts.append('new-session')
        if failures >= 5:
            alerts.append('login-failures')
    edge_certificates = list(edge_data.glob('caddy/certificates/**/*.crt'))
    certificates = [certificate] + edge_certificates
    if not edge_certificates or any(
        not item.is_file() or subprocess.run(
            ['openssl', 'x509', '-in', str(item), '-noout', '-checkend', str(30 * 86400)],
            capture_output=True, check=False,
        ).returncode for item in certificates
    ):
        alerts.append('certificate-expiry')
    if not restore_signal.is_file():
        alerts.append('restore-result-missing')
    else:
        proof = json.loads(restore_signal.read_text())
        observed = dt.datetime.fromisoformat(proof['observedAt'].replace('Z', '+00:00'))
        if proof.get('status') != 'pass' or now - observed > dt.timedelta(days=7):
            alerts.append('restore-failed-or-stale')
    print(json.dumps({'signal': 'website.security.baseline', 'observedAt': now.isoformat(),
                      'status': 'alert' if alerts else 'ok', 'alerts': sorted(set(alerts))}))
    return 1 if alerts else 0


if __name__ == '__main__':
    raise SystemExit(main())
