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


def main():
    import paho.mqtt.client as mqtt
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).resolve().parents[1] / '.env', override=False)
    host = os.getenv('MQTT_HOST', '127.0.0.1')
    port = int(os.getenv('MQTT_PORT', '1883'))
    simulated = os.getenv('FAN_SIMULATE', 'false').lower() == 'true'
    pin = int(os.getenv('FAN_GPIO', '23'))
    max_run = int(os.getenv('FAN_MAX_RUN_SECONDS', '300'))
    if not 1 <= max_run <= 3600:
        raise ValueError('FAN_MAX_RUN_SECONDS must be 1–3600')
    if pin in (17, 27, 22):
        raise ValueError('Fan GPIO conflicts with Phase 1 LEDs/buzzer')
    if simulated:
        output = SimulatedOutput()
        print('FAN SIMULATION: no physical GPIO is being driven.', flush=True)
    else:
        from gpiozero import DigitalOutputDevice
        from gpiozero.pins.lgpio import LGPIOFactory
        # GPIO Zero detects the Pi 5 RP1 chip; do not silently fall back to simulation.
        output = DigitalOutputDevice(pin, active_high=True, initial_value=False, pin_factory=LGPIOFactory())
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id='northstar-pi-fan', clean_session=True)
    username = os.getenv('MQTT_USER', '')
    if username:
        client.username_pw_set(username, os.getenv('MQTT_PASSWORD', ''))
    if os.getenv('MQTT_TLS', 'false').lower() == 'true':
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
            return
        controller.connected()
        client.subscribe('store/fan/set', qos=1)

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
