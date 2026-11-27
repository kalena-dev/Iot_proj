import test from 'node:test';
import assert from 'node:assert/strict';
import { brokerDiscoveryConfig, brokerServiceType } from '../src/discovery.js';

const interfaces = { wifi: [{ address: '192.0.2.42', family: 'IPv4', netmask: '255.255.255.0',
  cidr: '192.0.2.42/24', mac: '00:11:22:33:44:55', internal: false }] };

test('local TLS discovery advertises the configured certificate DNS identity', () => {
  const config = brokerDiscoveryConfig('mqtts://127.0.0.1:8883', 'northstar-broker.local', interfaces);
  assert.equal(config?.host, 'northstar-broker.local');
  assert.equal(config?.type, brokerServiceType);
  assert.equal(config?.port, 8883);
  assert.equal(config?.disableIPv6, true);
  assert.deepEqual(config?.txt, { version: '2', topics: 'Frig1,Frig2', tls: '1', serverName: 'northstar-broker.local' });
});

test('custom local TLS ports and interface addresses are supported', () => {
  assert.equal(brokerDiscoveryConfig('mqtts://192.0.2.42:2883', 'broker.local', interfaces)?.port, 2883);
  assert.equal(brokerDiscoveryConfig('mqtts://localhost', 'broker.local', interfaces)?.port, 8883);
});

test('remote and plaintext brokers are not falsely advertised as local TLS', () => {
  assert.equal(brokerDiscoveryConfig('mqtts://remote.example:8883', 'broker.local', interfaces), null);
  assert.equal(brokerDiscoveryConfig('mqtt://localhost:1883', 'broker.local', interfaces), null);
});

test('discovery records never contain MQTT credentials', () => {
  const config = brokerDiscoveryConfig('mqtts://private-user:private-password@localhost:8883', 'broker.local', interfaces);
  assert.doesNotMatch(JSON.stringify(config), /private-user|private-password/);
});
