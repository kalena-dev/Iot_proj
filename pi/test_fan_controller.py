import json
import unittest
from fan_controller import FanController, SimulatedOutput, MotorDriverOutput, validate_motor_config, mqtt_connection_settings, utc_iso


class FanTests(unittest.TestCase):
    def setUp(self):
        self.now = 1700000000.0
        self.output = SimulatedOutput()
        self.messages = []
        self.fan = FanController(self.output, lambda p: self.messages.append(json.loads(p)),
                                 simulated=True, max_run_seconds=10, clock=lambda: self.now)
        self.fan.connected()

    def command(self, on=True, command_id='test-command', age=15):
        return json.dumps({'commandId': command_id, 'on': on, 'expiresAt': utc_iso(self.now + age)})

    def test_confirmed_on_off(self):
        self.assertTrue(self.fan.apply(self.command()))
        self.assertEqual(self.output.value, 1)
        self.assertTrue(self.messages[-1]['on'])
        self.assertTrue(self.messages[-1]['simulated'])
        self.assertTrue(self.fan.apply(self.command(False, 'off-command')))
        self.assertEqual(self.output.value, 0)

    def test_retained_expired_malformed_commands_never_start(self):
        for command in [self.command(age=-1), self.command(age=100), self.command(on='true'), '{}', 'null', 'not json']:
            self.assertFalse(self.fan.apply(command))
        self.assertFalse(self.fan.apply(self.command(), retained=True))
        self.assertEqual(self.output.value, 0)

    def test_disconnect_and_reconnect_are_off(self):
        self.fan.apply(self.command())
        self.fan.disconnected()
        self.assertEqual(self.output.value, 0)
        self.assertFalse(self.fan.apply(self.command(command_id='offline-command')))
        self.fan.connected()
        self.assertFalse(self.messages[-1]['on'])

    def test_timeout_and_duplicate_do_not_extend_motor_run(self):
        payload = self.command()
        self.fan.apply(payload)
        self.now += 5
        self.assertFalse(self.fan.apply(payload))
        self.now += 6
        self.fan.tick()
        self.assertEqual(self.output.value, 0)
        self.assertIn('timeout', self.messages[-1]['reason'])


class TrackedOutput:
    def __init__(self, name, events, fail_on=False):
        self.name, self.events, self.fail_on = name, events, fail_on
        self.value = 0
        self.closed = False

    def on(self):
        self.events.append(self.name + '.on')
        if self.fail_on:
            raise RuntimeError('test output failure')
        self.value = 1

    def off(self):
        self.events.append(self.name + '.off')
        self.value = 0

    def close(self):
        self.events.append(self.name + '.close')
        self.closed = True


class MotorDriverTests(unittest.TestCase):
    def output(self, fail_enable=False):
        events = []
        enable = TrackedOutput('EN27', events, fail_on=fail_enable)
        in2 = TrackedOutput('IN2-17', events)
        return MotorDriverOutput(enable, in2), events

    def test_l293d_direction_before_enable_and_disable_before_direction(self):
        output, events = self.output()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off'])
        events.clear()
        output.on()
        self.assertEqual(events, ['IN2-17.on', 'EN27.on'])
        self.assertEqual(output.value, 1)
        events.clear()
        output.off()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off'])
        self.assertEqual(output.value, 0)

    def test_failed_enable_rolls_back_both_outputs(self):
        output, events = self.output(fail_enable=True)
        events.clear()
        with self.assertRaises(RuntimeError):
            output.on()
        self.assertEqual(events, ['IN2-17.on', 'EN27.on', 'EN27.off', 'IN2-17.off'])
        self.assertEqual(output.value, 0)

    def test_close_is_off_and_closes_both_lines(self):
        output, events = self.output()
        output.on()
        events.clear()
        output.close()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off', 'EN27.close', 'IN2-17.close'])
        self.assertTrue(output.enable.closed)
        self.assertTrue(output.in2.closed)
        self.assertEqual(output.value, 0)
        output.close()
        self.assertEqual(len(events), 4)

    def test_legacy_enable_only_output(self):
        events = []
        output = MotorDriverOutput(TrackedOutput('EN23', events))
        output.on()
        self.assertEqual(output.value, 1)
        output.off()
        self.assertEqual(output.value, 0)

    def test_disconnect_and_timeout_disable_both_real_output_roles(self):
        output, events = self.output()
        now = [1700000000.0]
        fan = FanController(output, lambda _: None, max_run_seconds=5, clock=lambda: now[0])
        fan.connected()
        command = lambda token: json.dumps({'commandId': token, 'on': True, 'expiresAt': utc_iso(now[0] + 15)})
        fan.apply(command('first'))
        now[0] += 6
        fan.tick()
        self.assertEqual(output.enable.value, 0)
        self.assertEqual(output.in2.value, 0)
        fan.apply(command('second'))
        fan.disconnected()
        self.assertEqual(output.enable.value, 0)
        self.assertEqual(output.in2.value, 0)

    def test_gpio17_27_require_status_gpio_disabled(self):
        settings = {'FAN_GPIO': '27', 'FAN_IN2_GPIO': '17'}
        with self.assertRaisesRegex(ValueError, 'conflicts'):
            validate_motor_config(settings)
        self.assertEqual(validate_motor_config({**settings, 'GPIO__ENABLED': 'false'}), (27, 17, 300))
        self.assertEqual(validate_motor_config({**settings, 'Gpio__Enabled': 'false'}), (27, 17, 300))

    def test_duplicate_invalid_or_custom_conflicting_pins_are_rejected(self):
        for settings in [
            {'FAN_GPIO': '23', 'FAN_IN2_GPIO': '23'},
            {'FAN_GPIO': '28'}, {'FAN_GPIO': '-1'}, {'FAN_MAX_RUN_SECONDS': '0'},
            {'FAN_GPIO': '5', 'GPIO__BLUELEDPIN': '5'}
        ]:
            with self.assertRaises(ValueError):
                validate_motor_config(settings)
        self.assertEqual(validate_motor_config({}), (23, None, 300))


class MqttSettingsTests(unittest.TestCase):
    def test_url_overrides_conflicting_legacy_host_port_and_tls(self):
        settings = {'MQTT_URL': 'mqtt://127.0.0.1:1883', 'MQTT_HOST': 'wrong-host',
                    'MQTT_PORT': '8883', 'MQTT_TLS': 'true'}
        self.assertEqual(mqtt_connection_settings(settings)[:3], ('127.0.0.1', 1883, False))

    def test_tls_url_default_port_and_url_credentials(self):
        self.assertEqual(mqtt_connection_settings({'MQTT_URL': 'mqtts://test-user:test%2Dpassword@broker.test'}),
                         ('broker.test', 8883, True, 'test-user', 'test-password'))
        self.assertEqual(mqtt_connection_settings({'MQTT_URL': 'mqtts://broker.test:2883',
                                                  'MQTT_USER': 'explicit-user', 'MQTT_PASSWORD': 'explicit-test'})[1:],
                         (2883, True, 'explicit-user', 'explicit-test'))

    def test_legacy_fields_and_invalid_urls(self):
        self.assertEqual(mqtt_connection_settings({})[:3], ('127.0.0.1', 1883, False))
        self.assertEqual(mqtt_connection_settings({'MQTT_TLS': 'true'})[:3], ('127.0.0.1', 8883, True))
        for url in ['https://broker.test', 'mqtt://', 'mqtt://broker.test:invalid', 'mqtt://broker.test:65536']:
            with self.assertRaises(ValueError):
                mqtt_connection_settings({'MQTT_URL': url})


if __name__ == '__main__':
    unittest.main()
