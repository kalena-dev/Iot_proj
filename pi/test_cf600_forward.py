import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / 'src/SmartStore/Scripts/cf600_forward.py'
spec = importlib.util.spec_from_file_location('cf600_forward', SCRIPT)
forward = importlib.util.module_from_spec(spec)
spec.loader.exec_module(forward)


class Cf600ForwardTests(unittest.TestCase):
    def run_forward(self, data, url='https://localhost:5081'):
        with tempfile.TemporaryDirectory() as directory:
            pairing = Path(directory) / 'pairing'
            pairing.write_text('paired-session')
            with patch.dict('os.environ', {'RFID_LOCAL_PAIRING_FILE': str(pairing),
                                           'RFID_SERVER_URL': url, 'RFID_API_KEY': 'private-key'}), \
                 patch.object(forward.sys, 'stdin', io.StringIO(data)), \
                 patch.object(forward, 'urlopen') as open_url, \
                 patch.object(forward.sys, 'stdout', io.StringIO()):
                forward.main()
                return open_url.call_args_list

    def test_valid_hex_is_normalized_and_delivered_to_configured_https(self):
        calls = self.run_forward('1 aabb\n1.1 aabb\n')
        self.assertEqual(1, len(calls))
        request = calls[0].args[0]
        self.assertEqual('https://localhost:5081/api/rfid/events', request.full_url)
        payload = json.loads(request.data)
        self.assertEqual('paired-session', payload['session'])
        self.assertEqual(['AABB'], payload['epcs'])
        self.assertTrue(payload['scanId'])
        self.assertEqual('reader', payload['source'])

    def test_rejects_oversized_odd_and_nonhex_codes(self):
        for epc in ('AA' * 13, 'AABBC', 'tag-001'):
            with self.subTest(epc=epc), self.assertRaises(ValueError):
                self.run_forward(f'1 {epc}\n')

    def test_http_never_disables_certificate_checks(self):
        for url in ('http://localhost:5080', 'http://store.example', 'https://user:secret@localhost:5081'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                self.run_forward('1 AABB\n', url)
