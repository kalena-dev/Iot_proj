import { hostname, networkInterfaces, type NetworkInterfaceInfo } from 'node:os';
import { Bonjour, type ServiceConfig } from 'bonjour-service';

// ESP32s query this DNS-SD service instead of embedding a server IP.
export const brokerServiceType = 'northstar-mqtt';

export function brokerDiscoveryConfig(mqttUrl: string, serverName: string,
  interfaces: NodeJS.Dict<NetworkInterfaceInfo[]> = networkInterfaces()): ServiceConfig | null {
  const url = new URL(mqttUrl);
  const addresses = Object.values(interfaces).flatMap(entries => entries ?? []).map(entry => entry.address);
  if (url.protocol !== 'mqtts:' || !['localhost', '127.0.0.1', '[::1]', ...addresses].includes(url.hostname)) return null;
  return { name: `Northstar MQTT ${hostname()}`, host: serverName,
    type: brokerServiceType, protocol: 'tcp', port: Number(url.port || 8883),
    disableIPv6: true, txt: { version: '2', topics: 'Frig1,Frig2', tls: '1', serverName } };
}

export function advertiseBroker(mqttUrl: string, serverName: string): () => Promise<void> {
  const config = brokerDiscoveryConfig(mqttUrl, serverName);
  if (!config) {
    console.warn('MQTT discovery skipped: only certificate-validated local TLS brokers can be advertised.');
    return async () => {};
  }
  const warn = () => console.warn('MQTT discovery unavailable; check multicast UDP5353 permissions/network.');
  const bonjour = new Bonjour({}, warn);
  const service = bonjour.publish(config);
  service.on('error', warn);
  service.on('up', () => console.log(`MQTT discovery advertising _${brokerServiceType}._tcp on port ${config.port}`));

  // Re-announce after DHCP/interface changes without restarting or reflashing.
  const signature = () => JSON.stringify(networkInterfaces());
  let previous = signature();
  const timer = setInterval(() => {
    const current = signature();
    if (current !== previous) {
      previous = current;
      service.stop(() => service.start());
    }
  }, 10000);
  timer.unref();
  return () => new Promise<void>(resolve => {
    clearInterval(timer);
    bonjour.unpublishAll(() => bonjour.destroy(resolve));
  });
}
