import { readFileSync } from 'node:fs';
import { isIP } from 'node:net';
import { z } from 'zod';

export const controlKeySchema = z.string().min(32).max(512).refine(value =>
  value.trim() === value && !/replace|placeholder|change.?me|your[_ -]|example|test.only/i.test(value)
  && new Set(value).size >= 8, 'Use a generated nonplaceholder API key of at least 32 characters.');

export function mqttTlsOptions(mqttUrl: string, caFile: string, serverName: string) {
  const url = new URL(mqttUrl);
  if (url.protocol !== 'mqtts:' || url.username || url.password)
    throw new Error('MQTT_URL must use mqtts without embedded credentials.');
  if (!caFile || !serverName || isIP(serverName) || !/^[a-zA-Z0-9][a-zA-Z0-9.-]*$/.test(serverName))
    throw new Error('MQTT_CA_FILE and a certificate DNS MQTT_SERVER_NAME are required.');
  return { ca: readFileSync(caFile), servername: serverName, rejectUnauthorized: true };
}
