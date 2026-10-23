import test from 'node:test';
import assert from 'node:assert/strict';
import { brokerDiscoveryConfig, brokerServiceType } from '../src/discovery.js';

const interfaces = { wifi: [{ address: '192.0.2.42', family: 'IPv4', netmask: '255.255.255.0',
  cidr: '192.0.2.42/24', mac: '00:11:22:33:44:55', internal: false }] };

test('local broker discovery uses the current hostname, not a fixed IP', () => {
  const config = brokerDiscoveryConfig('mqtt://127.0.0.1:1883', 'lab-server', interfaces);
  assert.equal(config?.host, 'northstar-lab-server.local');
  assert.equal(config?.type, brokerServiceType);
  assert.equal(config?.port, 1883);
  assert.equal(config?.disableIPv6, true);
  assert.deepEqual(config?.txt, { version: '1', topics: 'Frig1,Frig2' });
});

test('custom local broker ports and interface addresses are supported', () => {
  assert.equal(brokerDiscoveryConfig('mqtt://192.0.2.42:2883', 'other-server', interfaces)?.port, 2883);
  assert.equal(brokerDiscoveryConfig('mqtt://localhost', 'other-server', interfaces)?.port, 1883);
});

test('remote and TLS brokers are not advertised as local plain MQTT', () => {
  assert.equal(brokerDiscoveryConfig('mqtt://remote.example:1883', 'server', interfaces), null);
  assert.equal(brokerDiscoveryConfig('mqtts://localhost:8883', 'server', interfaces), null);
});

test('discovery records never contain MQTT credentials', () => {
  const config = brokerDiscoveryConfig('mqtt://private-user:private-password@localhost:1883', 'server', interfaces);
  assert.doesNotMatch(JSON.stringify(config), /private-user|private-password/);
});
