#!/usr/bin/env python3
"""Forward newline-delimited JSON EPC arrays from a separate reader adapter.

Use --sdk for continuous inventory with the vendor CLI.
"""
import argparse
from contextlib import closing
import json
import os
import sys
import time
from urllib.error import HTTPError
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


def sdk_batches(sdk_dir, port):
    from cf600_sdk import DEFAULT_SDK, inventory
    while True:
        yield from inventory(sdk_dir or DEFAULT_SDK, port)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', action='store_true', help='drive CDemo.out over its COM interface')
    parser.add_argument('--sdk-dir', help='directory containing CDemo.out and its shared libraries')
    parser.add_argument('--port', default='/dev/ttyUSB0')
    args = parser.parse_args()
    url = os.environ.get('RFID_SERVER_URL')
    key = os.environ.get('RFID_API_KEY', '')
    session = os.environ.get('RFID_SESSION')
    if not url or len(key) < 32 or not session:
        parser.exit(2, 'Set RFID_SERVER_URL, RFID_SESSION and a 32-character or longer RFID_API_KEY.\n')
    batches = (sdk_batches(args.sdk_dir, args.port) if args.sdk else
               (json.loads(line) for line in sys.stdin if line.strip()))
    try:
        with closing(batches):
            for epcs in batches:
                for attempt in range(3):
                    try:
                        send(epcs, url, key, session)
                        break
                    except HTTPError as error:
                        if error.code < 500 or attempt == 2:
                            raise
                    except OSError:
                        if attempt == 2:
                            raise
                    time.sleep(1)
    except KeyboardInterrupt:
        return 130
    except HTTPError as error:
        print(f'RFID delivery failed (HTTP {error.code}). Check the mapping (400), key/mode (401), or pairing (404).', file=sys.stderr)
        return 1
    except (ValueError, OSError) as error:
        print(f'RFID bridge stopped: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
