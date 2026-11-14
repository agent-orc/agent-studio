#!/usr/bin/env python3
"""Summarize phased CDP network traces into a per-chunk JS/CSS inventory.

Usage: python3 network-inventory.py <browser directory> <trace prefix> <output.json>
The prefix is, for example, JOB_RESULTS_DIR/chrome-network-after-20.
"""

import json
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse


def main() -> None:
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    browser = Path(sys.argv[1])
    prefix = Path(sys.argv[2])
    output = Path(sys.argv[3])
    phase_files = [('board-first-load', 'cold'), ('idle-prefetch', 'idle'), ('first-detail-open', 'detail')]
    requests = {}
    for phase, suffix in phase_files:
        for item in json.loads(Path(f'{prefix}-{suffix}.json').read_text()):
            event, data = item['event'], item['data']
            request_id = data['requestId']
            if event == 'Network.requestWillBeSent':
                path = unquote(urlparse(data['request']['url']).path).lstrip('/')
                if path.endswith(('.js', '.css')):
                    requests[request_id] = {'phase': phase, 'file': path, 'transferredBytes': 0}
            elif event == 'Network.loadingFinished' and request_id in requests:
                requests[request_id]['transferredBytes'] = data['encodedDataLength']
    result = {'note': 'Chrome CDP Network.loadingFinished encodedDataLength (gzip wire bytes including HTTP overhead); raw bytes from production build files. Requests assigned to the phase of Network.requestWillBeSent.', 'phases': {}}
    for phase, _ in phase_files:
        chunks = []
        for request in requests.values():
            if request['phase'] != phase:
                continue
            file = request['file']
            chunks.append({'file': file, 'rawBytes': (browser / file).stat().st_size,
                           'transferredBytes': request['transferredBytes']})
        chunks.sort(key=lambda chunk: chunk['transferredBytes'], reverse=True)
        result['phases'][phase] = {'count': len(chunks),
                                   'rawBytes': sum(chunk['rawBytes'] for chunk in chunks),
                                   'transferredBytes': sum(chunk['transferredBytes'] for chunk in chunks),
                                   'chunks': chunks}
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({name: {key: value for key, value in data.items() if key != 'chunks'}
                      for name, data in result['phases'].items()}, indent=2))


if __name__ == '__main__':
    main()
