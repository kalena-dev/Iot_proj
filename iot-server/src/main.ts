import { timingSafeEqual } from 'node:crypto';
import express from 'express';
import mqtt from 'mqtt';
import { MongoClient } from 'mongodb';
import { z } from 'zod';
import { EmailService } from './email.js';
import { Monitor } from './monitor.js';
import { MongoRepository } from './repository.js';
import { advertiseBroker } from './discovery.js';
import { loadConfiguration } from './configuration.js';
import { fridgeIds, fanCommandSchema, thresholdSchema, type FridgeId } from './model.js';

const environment = loadConfiguration();
const number = (fallback: number) => z.coerce.number().int().positive().default(fallback);
const env = z.object({
  MONGODB_URI: z.string().min(1), MONGODB_DATABASE: z.string().default('smart_store'),
  MQTT_URL: z.string().url().default('mqtt://127.0.0.1:1883'),
  MQTT_USER: z.string().default(''), MQTT_PASSWORD: z.string().default(''),
  MQTT_DISCOVERY_ENABLED: z.enum(['true', 'false']).default('true'),
  IOT_PORT: number(3100), IOT_API_KEY: z.string().default(''),
  DEFAULT_THRESHOLD: z.coerce.number().min(0).max(50).default(8),
  SENSOR_STALE_SECONDS: number(30), ALERT_COOLDOWN_SECONDS: number(900),
  REPLY_EXPIRY_SECONDS: number(900), EMAIL_RETRY_SECONDS: number(60),
  EMAIL_ENABLED: z.enum(['true', 'false']).default('false'),
  ALERT_TO: z.string().default(''), SMTP_HOST: z.string().default(''),
  SMTP_PORT: number(587), SMTP_USER: z.string().default(''), SMTP_PASSWORD: z.string().default(''),
  SMTP_FROM: z.string().default(''), IMAP_HOST: z.string().default(''),
  IMAP_PORT: number(993), IMAP_USER: z.string().default(''), IMAP_PASSWORD: z.string().default('')
}).safeParse(environment);
if (!env.success) {
  console.error('Missing/invalid configuration keys:', env.error.issues.map(issue => issue.path.join('.')).join(', '));
  process.exit(1);
}
const config = env.data;
if (config.EMAIL_ENABLED === 'true' && (!z.string().email().safeParse(config.ALERT_TO).success
  || !z.string().email().safeParse(config.SMTP_FROM).success
  || !['SMTP_HOST', 'SMTP_USER', 'SMTP_PASSWORD', 'IMAP_HOST', 'IMAP_USER', 'IMAP_PASSWORD'].every(k => environment[k]))) {
  console.error('Email enabled but SMTP, IMAP or recipient configuration is incomplete.');
  process.exit(1);
}
const mongo = new MongoClient(config.MONGODB_URI, { serverSelectionTimeoutMS: 8000 });
const mailer = new EmailService({ enabled: config.EMAIL_ENABLED === 'true',
  smtpHost: config.SMTP_HOST, smtpPort: config.SMTP_PORT, smtpUser: config.SMTP_USER,
  smtpPassword: config.SMTP_PASSWORD, from: config.SMTP_FROM, imapHost: config.IMAP_HOST,
  imapPort: config.IMAP_PORT, imapUser: config.IMAP_USER, imapPassword: config.IMAP_PASSWORD });
const broker = mqtt.connect(config.MQTT_URL, {
  clientId: 'northstar-iot-server', username: config.MQTT_USER || undefined,
  password: config.MQTT_PASSWORD || undefined, reconnectPeriod: 2000, clean: true,
  connectTimeout: 10000, queueQoSZero: false
});
// Attach before awaiting MongoDB initialization: MQTT can fail immediately.
broker.on('error', () => { console.warn('MQTT connection error; retrying. Check broker credentials/network.'); });
const monitor = new Monitor(new MongoRepository(mongo, config.MONGODB_DATABASE), mailer, {
  async publish(command) {
    if (!broker.connected) throw new Error('MQTT is disconnected');
    // Never retain motor commands: an old YES must not turn on a restarted Pi.
    await broker.publishAsync('store/fan/set', JSON.stringify(command), { qos: 1, retain: false });
  }
}, { defaultThreshold: config.DEFAULT_THRESHOLD, staleSeconds: config.SENSOR_STALE_SECONDS,
  cooldownSeconds: config.ALERT_COOLDOWN_SECONDS, replyExpirySeconds: config.REPLY_EXPIRY_SECONDS,
  emailRetrySeconds: config.EMAIL_RETRY_SECONDS, recipient: config.ALERT_TO });
try { await monitor.initialize(); }
catch { console.error('Cannot initialize MongoDB. Check MONGODB_URI, Atlas network access and database permissions.'); await broker.endAsync(); process.exit(1); }

const subscribe = async () => {
  try {
    await broker.subscribeAsync([...fridgeIds, 'store/fan/status'], { qos: 1 });
    monitor.brokerConnected = true;
  } catch { console.warn('Cannot subscribe to MQTT topics.'); }
};
broker.on('connect', () => { void subscribe(); });
if (broker.connected) await subscribe();
broker.on('offline', () => { monitor.brokerConnected = false; });
broker.on('close', () => { monitor.brokerConnected = false; });
broker.on('message', (topic, payload, packet) => {
  if (payload.length > 4096) return;
  const work = topic === 'store/fan/status'
    ? monitor.acceptFanStatus(payload.toString())
    : monitor.ingest(topic, payload.toString(), packet.retain);
  void work.catch(() => console.warn(`Cannot process MQTT data on ${topic}; check payload format and database availability.`));
});

const app = express();
app.disable('x-powered-by');
app.use(express.json({ limit: '4kb' }));
app.use((req, res, next) => {
  if (config.IOT_API_KEY) {
    const supplied = Buffer.from(req.header('X-Iot-Key') ?? '');
    const expected = Buffer.from(config.IOT_API_KEY);
    if (supplied.length !== expected.length || !timingSafeEqual(supplied, expected)) {
      res.status(401).json({ error: 'Unauthorized' }); return;
    }
  }
  res.setHeader('Cache-Control', 'no-store');
  next();
});
app.get('/api/status', async (_req, res) => { res.json(await monitor.snapshot()); });
app.put('/api/refrigerators/:id/threshold', async (req, res) => {
  const parsed = thresholdSchema.safeParse(req.body);
  if (!fridgeIds.includes(req.params.id as FridgeId) || !parsed.success) {
    res.status(400).json({ error: 'Choose Frig1/Frig2 and a threshold from 0 to 50 °C.' }); return;
  }
  await monitor.setThreshold(req.params.id as FridgeId, parsed.data.threshold);
  res.json({ message: 'Threshold saved in MongoDB.' });
});
app.post('/api/fan', async (req, res) => {
  const parsed = fanCommandSchema.safeParse(req.body);
  if (!parsed.success) { res.status(400).json({ error: 'on must be a boolean.' }); return; }
  try { res.json(await monitor.requestFan(parsed.data.on)); }
  catch { res.status(409).json({ error: 'Fan controller is unavailable or a command is awaiting confirmation.' }); }
});
app.use((_error: unknown, _req: express.Request, res: express.Response, _next: express.NextFunction) => {
  res.status(503).json({ error: 'IoT service request failed. Check service logs and MongoDB.' });
});
// Only the existing Razor app accesses this API; do not expose it to the LAN.
const server = app.listen(config.IOT_PORT, '127.0.0.1', () => console.log(`IoT service listening on 127.0.0.1:${config.IOT_PORT}`));
const stopDiscovery = config.MQTT_DISCOVERY_ENABLED === 'true'
  ? advertiseBroker(config.MQTT_URL) : async () => {};
let polling = false;
const poll = async () => {
  if (polling) return;
  polling = true;
  try { await monitor.tick(); await mailer.poll(monitor); }
  catch { console.warn('Alert processing failed; check database availability.'); }
  finally { polling = false; }
};
const timer = setInterval(() => { void poll(); }, 10000);
void poll();
async function stop() {
  clearInterval(timer); server.close(); mailer.close();
  await stopDiscovery(); await broker.endAsync(); await mongo.close(); process.exit(0);
}
process.once('SIGINT', () => { void stop(); });
process.once('SIGTERM', () => { void stop(); });
