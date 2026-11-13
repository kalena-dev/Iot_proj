import json
import unittest
from unittest.mock import patch, MagicMock
import rfid_bridge


class RfidBridgeTests(unittest.TestCase):
    def test_forwards_actual_batch_and_private_credentials(self):
        with patch('urllib.request.urlopen') as open_url:
            open_url.return_value.__enter__.return_value.status = 200
            self.assertEqual(200, rfid_bridge.send(['AABB', 'CCDD'], 'https://store.example', 'private-key', 'session'))
            request = open_url.call_args.args[0]
            self.assertEqual({'session': 'session', 'epcs': ['AABB', 'CCDD']}, json.loads(request.data))
            self.assertEqual('private-key', request.get_header('X-rfid-key'))
            self.assertEqual(10, open_url.call_args.kwargs['timeout'])

    def test_rejects_insecure_remote_transport_and_invalid_tags(self):
        for epcs, url in [(['AABB'], 'http://store.example'), (['-238'], 'https://store.example'),
                          ([], 'https://store.example'), (['DEMO-EPC-001'], 'https://store.example')]:
            with self.subTest(epcs=epcs, url=url), patch('urllib.request.urlopen') as open_url:
                with self.assertRaises(ValueError):
                    rfid_bridge.send(epcs, url, 'key', 'session')
                open_url.assert_not_called()
