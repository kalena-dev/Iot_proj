import { randomUUID } from 'node:crypto';
import { fridgeIds, telemetrySchema, fanStatusSchema, type Alert, type FanCommand, type FanPublisher,
  type FanStatus, type FridgeId, type Mailer, type MonitorOptions, type Notification,
  type Reading, type Repository } from './model.js';

export class Monitor {
  private readings = new Map<FridgeId, Reading>();
  private thresholds = new Map<FridgeId, number>();
  private alerts = new Map<FridgeId, Alert>();
  private fan: FanStatus | null = null;
  private fanReceivedAt = 0;
  private pending: FanCommand | null = null;
  private queue: Promise<unknown> = Promise.resolve();
  brokerConnected = false;
  emailConnected = false;

  constructor(private readonly repo: Repository, private readonly mailer: Mailer,
    private readonly publisher: FanPublisher, private readonly options: MonitorOptions,
    private readonly now: () => number = Date.now) {}

  // MQTT messages, HTTP changes and email replies share one ordered pipeline.
  private serial<T>(work: () => Promise<T>): Promise<T> {
    const next = this.queue.then(work);
    this.queue = next.catch(() => undefined);
    return next;
  }
  async initialize() {
    await this.repo.initialize(this.options.defaultThreshold);
    for (const setting of await this.repo.settings()) this.thresholds.set(setting._id, setting.threshold);
    for (const id of fridgeIds) {
      const reading = await this.repo.latestReading(id);
      if (reading) this.readings.set(id, reading);
      const alert = await this.repo.latestAlert(id);
      if (alert) this.alerts.set(id, alert);
    }
  }
  private iso() { return new Date(this.now()).toISOString(); }
  private fresh(reading: Reading) {
    return this.now() - Date.parse(reading.receivedAt) <= this.options.staleSeconds * 1000;
  }
  private async notify(kind: Notification['kind'], message: string, fridge?: FridgeId) {
    await this.repo.notification({ _id: randomUUID(), createdAt: this.iso(), kind, message, fridge });
  }
  ingest(topic: string, payload: string, retained = false) {
    return this.serial(async () => {
      // Retained telemetry must never look like a new measurement or trigger an email.
      if (!fridgeIds.includes(topic as FridgeId) || retained) return false;
      const fridge = topic as FridgeId;
      const parsed = telemetrySchema.safeParse(JSON.parse(payload));
      if (!parsed.success) throw new Error('Invalid DHT11 telemetry');
      const { temperature, humidity, sampleId } = parsed.data;
      const reading: Reading = { _id: sampleId ? `${fridge}:${sampleId}` : randomUUID(),
        fridge, temperature, humidity, receivedAt: this.iso() };
      if (!await this.repo.saveReading(reading)) return false;
      this.readings.set(fridge, reading);
      await this.evaluate(fridge);
      return true;
    });
  }
  setThreshold(fridge: FridgeId, threshold: number) {
    return this.serial(async () => {
      if (!Number.isFinite(threshold) || threshold < 0 || threshold > 50) throw new Error('Threshold must be 0–50 °C');
      await this.repo.setThreshold(fridge, threshold);
      this.thresholds.set(fridge, threshold);
      await this.evaluate(fridge);
    });
  }
  private async evaluate(fridge: FridgeId) {
    const reading = this.readings.get(fridge);
    const threshold = this.thresholds.get(fridge)!;
    if (!reading || !this.fresh(reading) || reading.temperature <= threshold) return;
    const previous = this.alerts.get(fridge);
    // At most one email per fridge/cooldown, including while a reply is outstanding.
    if (previous && this.now() - Date.parse(previous.createdAt) < this.options.cooldownSeconds * 1000) return;
    if (previous && !previous.decision && Date.parse(previous.expiresAt) > this.now()) return;
    const alert: Alert = { _id: randomUUID(), fridge, temperature: reading.temperature, threshold,
      recipient: this.options.recipient.toLowerCase(), createdAt: this.iso(),
      expiresAt: new Date(this.now() + this.options.replyExpirySeconds * 1000).toISOString(), emailState: 'pending' };
    await this.repo.saveAlert(alert);
    this.alerts.set(fridge, alert);
    await this.notify('alert', `${fridge}: ${reading.temperature} °C exceeds ${threshold} °C. Email alert queued. Fan unchanged.`, fridge);
    await this.sendAlert(alert);
  }
  private async sendAlert(alert: Alert) {
    const reading = this.readings.get(alert.fridge);
    if (alert.emailState === 'sent' || alert.decision || Date.parse(alert.expiresAt) <= this.now()
      || !reading || !this.fresh(reading) || reading.temperature <= this.thresholds.get(alert.fridge)!) return;
    alert.lastAttemptAt = this.iso();
    await this.repo.saveAlert(alert);
    try {
      await this.mailer.send(alert);
    } catch {
      await this.notify('error', `${alert.fridge}: email delivery failed or email is not configured. Will retry; fan remains unchanged.`, alert.fridge);
      return;
    }
    alert.emailState = 'sent';
    await this.repo.saveAlert(alert);
    await this.notify('email', `${alert.fridge}: email sent at ${alert.temperature} °C. Reply YES to request the fan; any other reply leaves it unchanged.`, alert.fridge);
  }
  tick() {
    return this.serial(async () => {
      if (this.pending && Date.parse(this.pending.expiresAt) <= this.now()) {
        this.pending = null;
        await this.notify('error', 'Fan command timed out. No matching Raspberry Pi acknowledgement was received.');
      }
      for (const fridge of fridgeIds) {
        const alert = this.alerts.get(fridge);
        if (alert && (!alert.lastAttemptAt || this.now() - Date.parse(alert.lastAttemptAt) >= this.options.emailRetrySeconds * 1000))
          await this.sendAlert(alert);
        await this.evaluate(fridge);
      }
    });
  }
  acceptFanStatus(payload: string) {
    return this.serial(async () => {
      const status = fanStatusSchema.parse(JSON.parse(payload));
      // A retained old ON status is not hardware confirmation after a restart.
      const age = this.now() - Date.parse(status.timestamp);
      if (status.online && (age < -5000 || age > 30000)) return;
      this.fan = status;
      this.fanReceivedAt = this.now();
      if (this.pending && status.commandId === this.pending.commandId && status.online && status.on === this.pending.on) {
        this.pending = null;
        await this.notify('fan', `Raspberry Pi confirmed fan ${status.on ? 'ON' : 'OFF'}${status.simulated ? ' (SIMULATION)' : ''}.`);
      }
    });
  }
  private fanOnline() {
    return this.brokerConnected && !!this.fan?.online && this.now() - this.fanReceivedAt <= 30000;
  }
  private async commandFan(on: boolean) {
    if (!this.fanOnline()) throw new Error('Fan controller is offline or stale. Check the Raspberry Pi.');
    if (this.pending) throw new Error('A fan command is already awaiting confirmation.');
    const command: FanCommand = { commandId: randomUUID(), on,
      expiresAt: new Date(this.now() + 15000).toISOString() };
    this.pending = command;
    try { await this.publisher.publish(command); }
    catch (error) { this.pending = null; throw error; }
    await this.notify('fan', `Fan ${on ? 'ON' : 'OFF'} requested; awaiting Raspberry Pi confirmation.`);
    return command;
  }
  requestFan(on: boolean) { return this.serial(() => this.commandFan(on)); }
  reply(sender: string, subject: string, text: string) {
    return this.serial(async () => {
      const token = subject.match(/\[Northstar:([a-f0-9-]{36})\]/i)?.[1];
      const alert = [...this.alerts.values()].find(a => a._id === token);
      if (!alert || alert.emailState !== 'sent' || alert.decision || Date.parse(alert.expiresAt) <= this.now()
        || sender.toLowerCase() !== alert.recipient) return 'ignored';
      const answer = text.trimStart().split(/\r?\n/)[0].trim().toUpperCase();
      // Only an exact YES on the first nonblank line grants permission. NO is recorded.
      if (answer !== 'YES' && answer !== 'NO') return 'ignored';
      if (answer === 'YES') {
        const reading = this.readings.get(alert.fridge);
        if (!reading || !this.fresh(reading) || reading.temperature <= this.thresholds.get(alert.fridge)!) {
          await this.notify('email', `${alert.fridge}: YES ignored because the reading is stale or temperature has recovered.`, alert.fridge);
          alert.decision = 'NO';
        } else {
          await this.commandFan(true); // If offline, leave email unread for a later retry.
          alert.decision = 'YES';
        }
      } else alert.decision = 'NO';
      alert.decidedAt = this.iso();
      await this.repo.saveAlert(alert);
      await this.notify('email', `${alert.fridge}: reply processed (${alert.decision}).${alert.decision === 'NO' ? ' No fan action taken.' : ''}`, alert.fridge);
      return 'processed';
    });
  }
  async snapshot() {
    await this.queue;
    return {
      timestamp: this.iso(), brokerConnected: this.brokerConnected, emailConnected: this.emailConnected,
      refrigerators: fridgeIds.map(id => {
        const reading = this.readings.get(id);
        const threshold = this.thresholds.get(id)!;
        const stale = !reading || !this.fresh(reading);
        return { id, threshold, temperature: reading?.temperature ?? null, humidity: reading?.humidity ?? null,
          receivedAt: reading?.receivedAt ?? null, stale,
          status: stale ? 'offline' : reading!.temperature > threshold ? 'alert' : 'normal' };
      }),
      fan: { online: this.fanOnline(), on: this.fanOnline() ? this.fan!.on : null,
        simulated: this.fan?.simulated ?? false, pending: this.pending?.on ?? null },
      notifications: await this.repo.notifications()
    };
  }
}
