#!/usr/bin/env python3
"""MQTT fan actuator. GPIO is opened only by main(), never by tests/imports."""
import json
import os
import signal
import threading
import time
from collections import deque
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse, unquote


def utc_iso(now=None):
    return datetime.fromtimestamp(time.time() if now is None else now, timezone.utc).isoformat().replace('+00:00', 'Z')


class FanController:
    def __init__(self, output, publish, simulated=False, max_run_seconds=300, clock=time.time):
        self.output = output
        self.publish = publish
        self.simulated = simulated
        self.max_run_seconds = max_run_seconds
        self.clock = clock
        self.online = False
        self.command_id = None
        self.deadline = None
        self.seen = deque(maxlen=128)
        self.lock = threading.RLock()
        self.output.off()

    def status(self, reason='heartbeat'):
        self.publish(json.dumps({
            'online': self.online, 'on': bool(self.output.value), 'simulated': self.simulated,
            'commandId': self.command_id, 'timestamp': utc_iso(self.clock()), 'reason': reason
        }))

    def connected(self):
        with self.lock:
            self.output.off()  # Never restore the previous motor state after reconnect.
            self.deadline = None
            self.command_id = None
            self.online = True
            self.status('connected; safe OFF')

    def disconnected(self):
        with self.lock:
            self.output.off()
            self.online = False
            self.deadline = None

    def apply(self, payload, retained=False):
        with self.lock:
            if retained or not self.online or len(payload) > 4096:
                return False
            try:
                command = json.loads(payload)
                command_id = command['commandId']
                on = command['on']
                expires = datetime.fromisoformat(command['expiresAt'].replace('Z', '+00:00')).timestamp()
                if not isinstance(command_id, str) or not 1 <= len(command_id) <= 80 or type(on) is not bool:
                    return False
                if not self.clock() < expires <= self.clock() + 60:
                    return False
            except (KeyError, TypeError, ValueError, AttributeError, OverflowError):
                return False
            if command_id in self.seen:
                self.status('duplicate command ignored')
                return False
            if on:
                self.output.on()
                self.deadline = self.clock() + self.max_run_seconds
            else:
                self.output.off()
                self.deadline = None
            self.command_id = command_id
            self.seen.append(command_id)
            self.status('GPIO output applied')
            return True

    def tick(self):
        with self.lock:
            if self.deadline is not None and self.clock() >= self.deadline:
                self.output.off()
                self.deadline = None
                self.status('safety run timeout; OFF')
            elif self.online:
                self.status()


class SimulatedOutput:
    value = 0
    def on(self): self.value = 1
    def off(self): self.value = 0
    def close(self): self.off()


class MotorDriverOutput:
    """Enable + optional IN2 output; IN1 must be held LOW externally.

    Set direction before enabling. Disable the bridge before changing direction.
    The value confirms GPIO state only, never mechanical motor rotation.
    """
    def __init__(self, enable, in2=None):
        self.enable = enable
        self.in2 = in2
        self.closed = False
        self.off()

    @property
    def value(self):
        return int(not self.closed and bool(self.enable.value)
                   and (self.in2 is None or bool(self.in2.value)))

    def on(self):
        try:
            if self.in2 is not None:
                self.in2.on()
            self.enable.on()
        except Exception:
            try:
                self.off()
            except Exception:
                pass
            raise

    def off(self):
        if self.closed:
            return
        try:
            self.enable.off()
        finally:
            if self.in2 is not None:
                self.in2.off()

    def close(self):
        if self.closed:
            return
        try:
            self.off()
        finally:
            try:
                self.enable.close()
            finally:
                try:
                    if self.in2 is not None:
                        self.in2.close()
                finally:
                    self.closed = True


def validate_motor_config(environment):
    # ASP.NET configuration keys are case-insensitive, including GPIO__ENABLED.
    settings = {key.upper(): value for key, value in environment.items()}
    pin = int(settings.get('FAN_GPIO', '23'))
    in2_text = settings.get('FAN_IN2_GPIO', '').strip()
    in2_pin = int(in2_text) if in2_text else None
    max_run = int(settings.get('FAN_MAX_RUN_SECONDS', '300'))
    pins = [pin] + ([] if in2_pin is None else [in2_pin])
    if any(number < 0 or number > 27 for number in pins):
        raise ValueError('Motor pins must be BCM GPIO numbers from 0 to 27')
    if len(set(pins)) != len(pins):
        raise ValueError('Motor enable and IN2 must use different GPIO pins')
    if not 1 <= max_run <= 3600:
        raise ValueError('FAN_MAX_RUN_SECONDS must be 1–3600')
    if settings.get('GPIO__ENABLED', 'true').strip().lower() != 'false':
        reserved = {int(settings.get('GPIO__BLUELEDPIN', '17')),
                    int(settings.get('GPIO__REDLEDPIN', '27'))}
        if settings.get('GPIO__BUZZERENABLED', 'true').strip().lower() != 'false':
            reserved.add(int(settings.get('GPIO__BUZZERPIN', '22')))
        if set(pins) & reserved:
            raise ValueError('Motor GPIO conflicts with status LEDs/buzzer. Set GPIO__ENABLED=false or relocate those status outputs.')
    return pin, in2_pin, max_run


def create_motor_output(pin, in2_pin):
    from gpiozero import DigitalOutputDevice
    from gpiozero.pins.lgpio import LGPIOFactory
    factory = LGPIOFactory()
    # Open EN first, LOW, so IN2 initialization can never energize the bridge.
    enable = DigitalOutputDevice(pin, active_high=True, initial_value=False, pin_factory=factory)
    in2 = None
    try:
        if in2_pin is not None:
            in2 = DigitalOutputDevice(in2_pin, active_high=True, initial_value=False, pin_factory=factory)
        return MotorDriverOutput(enable, in2)
    except Exception:
        try:
            enable.close()
        finally:
            if in2 is not None:
                in2.close()
        raise


def mqtt_connection_settings(environment):
    """Use the same MQTT_URL as TypeScript; legacy fields are fallback only."""
    url = environment.get('MQTT_URL', '').strip()
    parsed = None
    if url:
        parsed = urlparse(url)
        if parsed.scheme not in ('mqtt', 'mqtts') or not parsed.hostname:
            raise ValueError('MQTT_URL must use mqtt:// or mqtts:// with a broker hostname')
        tls = parsed.scheme == 'mqtts'
        host = parsed.hostname
        try:
            port = parsed.port or (8883 if tls else 1883)
        except ValueError:
            raise ValueError('MQTT_URL must contain a valid broker port') from None
    else:
        tls = environment.get('MQTT_TLS', 'false').strip().lower() == 'true'
        host = environment.get('MQTT_HOST', '127.0.0.1')
        port = int(environment.get('MQTT_PORT', '8883' if tls else '1883'))
    if not 1 <= port <= 65535:
        raise ValueError('MQTT port must be 1–65535')
    username = environment.get('MQTT_USER') or (unquote(parsed.username or '') if parsed else '')
    password = environment.get('MQTT_PASSWORD') or (unquote(parsed.password or '') if parsed else '')
    return host, port, tls, username, password


def main():
    import paho.mqtt.client as mqtt
    from dotenv import load_dotenv
    config_home = Path(os.getenv('XDG_CONFIG_HOME', str(Path.home() / '.config')))
    for path in [os.getenv('MONGODB_ENV_FILE'), Path(__file__).resolve().parents[1] / '.env',
                 config_home / 'northstar' / 'credentials.env']:
        if path:
            load_dotenv(path, override=False)
    host, port, mqtt_tls, username, password = mqtt_connection_settings(os.environ)
    simulated = os.getenv('FAN_SIMULATE', 'false').lower() == 'true'
    pin, in2_pin, max_run = validate_motor_config(os.environ)
    if simulated:
        output = SimulatedOutput()
        print('FAN SIMULATION: no physical GPIO is being driven.', flush=True)
    else:
        if os.getenv('FAN_WIRING_CONFIRMED', 'false').strip().lower() != 'true':
            raise ValueError('Verify driver inputs, common GND and external motor power, then set FAN_WIRING_CONFIRMED=true. Never connect a GPIO to +Vmotor.')
        # GPIO Zero detects the Pi 5 RP1 chip; never silently simulate on failure.
        output = create_motor_output(pin, in2_pin)
        direction = f', IN2=BCM{in2_pin}' if in2_pin is not None else ''
        print(f'FAN REAL GPIO: EN=BCM{pin}{direction}; initialized safe OFF.', flush=True)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id='northstar-pi-fan', clean_session=True)
    if username:
        client.username_pw_set(username, password)
    if mqtt_tls:
        client.tls_set()
    client.reconnect_delay_set(min_delay=1, max_delay=10)
    client.will_set('store/fan/status', json.dumps({
        'online': False, 'on': False, 'simulated': simulated, 'commandId': None,
        'timestamp': utc_iso(), 'reason': 'MQTT connection lost'
    }), qos=1, retain=True)
    controller = FanController(output,
        lambda payload: client.publish('store/fan/status', payload, qos=1, retain=True),
        simulated=simulated, max_run_seconds=max_run)
    stop = threading.Event()

    def on_connect(_client, _userdata, _flags, reason_code, _properties):
        if reason_code.is_failure:
            controller.disconnected()
            print('Fan MQTT sign-in rejected; check broker credentials.', flush=True)
            return
        controller.connected()
        client.subscribe('store/fan/set', qos=1)
        print('Fan MQTT connected; safe OFF status published.', flush=True)

    def on_connect_fail(_client, _userdata):
        controller.disconnected()
        print('Fan MQTT connection failed; output OFF. Check MQTT_URL/network/TLS; retrying.', flush=True)

    def on_disconnect(_client, _userdata, _flags, _reason, _properties):
        controller.disconnected()

    def on_message(_client, _userdata, message):
        try:
            if message.topic == 'store/fan/set':
                controller.apply(message.payload, retained=message.retain)
        except Exception:
            controller.disconnected()
            print('Fan output failed; forced OFF. Check GPIO wiring/permissions.', flush=True)

    client.on_connect = on_connect
    client.on_connect_fail = on_connect_fail
    client.on_disconnect = on_disconnect
    client.on_message = on_message
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda _signum, _frame: stop.set())
    try:
        client.connect_async(host, port, keepalive=15)
        client.loop_start()
        while not stop.wait(5):
            controller.tick()
    finally:
        controller.disconnected()
        if client.is_connected():
            info = client.publish('store/fan/status', json.dumps({
                'online': False, 'on': False, 'simulated': simulated, 'commandId': None,
                'timestamp': utc_iso(), 'reason': 'controller stopped'
            }), qos=1, retain=True)
            info.wait_for_publish(timeout=2)
        client.disconnect()
        client.loop_stop()
        output.close()


if __name__ == '__main__':
    main()
