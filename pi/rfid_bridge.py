#!/usr/bin/env python3
"""Forward newline-delimited JSON EPC arrays from a separate reader adapter.

No CF600 SDK calls or fabricated fallback readings. Retrying a batch is safe.
"""
import json
import os
import sys
import time
import urllib.request
from urllib.parse import urlparse


def send(epcs, url, key, session):
    if not isinstance(epcs, list) or not 1 <= len(epcs) <= 100:
        raise ValueError('Expected an array of 1–100 EPCs')
    if any(not isinstance(epc, str) or not 4 <= len(epc) <= 128
           or len(epc) % 2 or any(c not in '0123456789abcdefABCDEF' for c in epc)
           for epc in epcs):
        raise ValueError('Expected hexadecimal EPC bytes')
    parsed = urlparse(url)
    if parsed.scheme != 'https' and not (parsed.scheme == 'http' and parsed.hostname in ('localhost', '127.0.0.1', '::1')):
        raise ValueError('Use HTTPS except on loopback')
    request = urllib.request.Request(url.rstrip('/') + '/api/rfid/events',
        data=json.dumps({'session': session, 'epcs': epcs}).encode(),
        headers={'Content-Type': 'application/json', 'X-Rfid-Key': key}, method='POST')
    with urllib.request.urlopen(request, timeout=10) as response:
        return response.status


def main():
    url = os.environ['RFID_SERVER_URL']
    key = os.environ['RFID_API_KEY']
    session = os.environ['RFID_SESSION']
    if len(key) < 32:
        raise ValueError('RFID_API_KEY must be at least 32 characters')
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            epcs = json.loads(line)
            for attempt in range(3):
                try:
                    send(epcs, url, key, session)
                    break
                except (OSError, TimeoutError):
                    if attempt == 2:
                        raise
                    time.sleep(1)
        except (ValueError, OSError, TimeoutError):
            # Do not log secrets, session capabilities, or raw RFID contents.
            print('RFID batch rejected or delivery failed; check pairing, mapping and connectivity.', file=sys.stderr)
            return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
