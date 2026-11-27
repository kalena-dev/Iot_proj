import json
import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock
import rfid_bridge


class RfidBridgeTests(unittest.TestCase):
    def test_forwards_actual_batch_and_private_credentials(self):
        with patch('urllib.request.urlopen') as open_url:
            open_url.return_value.__enter__.return_value.status = 200
            self.assertEqual(200, rfid_bridge.send(['AABB', 'CCDD'], 'https://store.example', 'private-key', 'session'))
            request = open_url.call_args.args[0]
            self.assertEqual({'session': 'session', 'epcs': ['AABB', 'CCDD'], 'source': 'reader'}, json.loads(request.data))
            self.assertEqual('private-key', request.get_header('X-rfid-key'))
            self.assertEqual(10, open_url.call_args.kwargs['timeout'])

    def test_rejects_insecure_remote_transport_and_invalid_tags(self):
        for epcs, url in [(['AABB'], 'http://store.example'), (['AABB'], 'http://localhost:5080'),
                          (['-238'], 'https://store.example'), ([], 'https://store.example'),
                          (['DEMO-EPC-001'], 'https://store.example'), (['A' * 26], 'https://store.example'),
                          (['AABBC'], 'https://store.example'), (['AABB'], 'https://user:password@store.example')]:
            with self.subTest(epcs=epcs, url=url), patch('urllib.request.urlopen') as open_url:
                with self.assertRaises(ValueError):
                    rfid_bridge.send(epcs, url, 'key', 'session')
                open_url.assert_not_called()

    def test_accepts_maximum_whole_byte_epc(self):
        with patch('urllib.request.urlopen') as open_url:
            open_url.return_value.__enter__.return_value.status = 200
            self.assertEqual(200, rfid_bridge.send(['AB' * 12], 'https://localhost:5081', 'key', 'session'))

    def test_manual_transfer_keeps_manual_provenance(self):
        with patch('urllib.request.urlopen') as open_url:
            open_url.return_value.__enter__.return_value.status = 200
            rfid_bridge.send(['AABB'], 'https://localhost:5081', 'key', 'session', source='manual')
            self.assertEqual('manual', json.loads(open_url.call_args.args[0].data)['source'])

    def test_launcher_requires_https_without_disabling_advisory_checks(self):
        launcher = (Path(__file__).resolve().parent / 'start-checkout.sh').read_text()
        self.assertIn('https://localhost:5081', launcher)
        self.assertIn('ASPNETCORE_Kestrel__Certificates__Default__Path:?', launcher)
        self.assertIn('--setenv=ASPNETCORE_Kestrel__Certificates__Default__Path', launcher)
        self.assertIn('--cacert \"$SSL_CERT_FILE\"', launcher)
        self.assertIn('--setenv=SSL_CERT_FILE', launcher)
        self.assertNotIn('NuGetAudit=false', launcher)
        self.assertNotIn('UseHttpsRedirection=false', launcher)
        self.assertNotIn('--insecure', launcher)
