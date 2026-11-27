import { createHash, randomUUID } from 'node:crypto';
import { fridgeIds, telemetrySchema, fanStatusSchema, type Actor, type Alert, type CommandRecord,
  type FanPublisher, type FanStatus, type FridgeId, type Mailer, type MonitorOptions,
  type Notification, type Reading, type Repository } from './model.js';

const localActor: Actor = { id: 'local-service' };

export class Monitor {
  private readings = new Map<FridgeId, Reading>();
  private thresholds = new Map<FridgeId, number>();
  private alerts = new Map<FridgeId, Alert>();
  private commands = new Map<string, CommandRecord>();
  private fan: FanStatus | null = null;
  private fanReceivedAt = 0;
  private queue: Promise<unknown> = Promise.resolve();
  private ingress = new Map<FridgeId, Reading>();
  private telemetryWork: Promise<boolean> | null = null;
  private mailWork = new Map<FridgeId, Promise<void>>();
  brokerConnected = false;
  emailConnected = false;

  constructor(private readonly repo: Repository, private readonly mailer: Mailer,
    private readonly publisher: FanPublisher, private readonly options: MonitorOptions,
    private readonly now: () => number = Date.now) {}

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
      if (alert && alert.recipient.toLowerCase() === this.options.recipient.trim().toLowerCase()) {
        this.alerts.set(id, alert);
      } else if (alert && !alert.decision) {
        await this.repo.consumeAlert(alert._id, 'NO', this.iso());
        await this.notify('email', `${id}: previous approval cancelled because the alert recipient changed. Fan unchanged.`, id);
      }
    }
    const recovered = await this.repo.outstandingCommands();
    const stopping = recovered.some(command => !command.on);
    for (const command of recovered) {
      // Never replay an ON after a crash, even if publication was interrupted.
      command.state = stopping && command.on ? 'superseded'
        : Date.parse(command.expiresAt) <= this.now() ? 'expired' : 'uncertain';
      await this.repo.updateCommand(command);
      if (['queued', 'dispatching', 'pending', 'uncertain'].includes(command.state)) this.commands.set(command.commandId, command);
      await this.commandNotice(command, 'Service restarted; command delivery requires controller confirmation.');
    }
  }
  private iso() { return new Date(this.now()).toISOString(); }
  private fresh(reading: Reading) {
    const age = this.now() - Date.parse(reading.receivedAt);
    return age >= 0 && age <= this.options.staleSeconds * 1000;
  }
  private async notify(kind: Notification['kind'], message: string, fridge?: FridgeId,
    detail: Pick<Notification, 'actor' | 'commandId' | 'outcome' | 'action'> = {}) {
    await this.repo.notification({ _id: randomUUID(), createdAt: this.iso(), kind, message, fridge, ...detail });
  }
  private commandNotice(command: CommandRecord, message?: string) {
    message ??= command.state === 'uncertain' ? 'Fan command delivery uncertain; awaiting Raspberry Pi confirmation.'
      : command.state === 'confirmed' ? `Raspberry Pi confirmed fan ${command.on ? 'ON' : 'OFF'}.`
        : command.state === 'superseded' ? 'Pending ON superseded by OFF.'
          : `Fan ${command.on ? 'ON' : 'OFF'} requested; awaiting Raspberry Pi confirmation.`;
    return this.notify('fan', message, command.fridge, {
      actor: command.actor, commandId: command.commandId, outcome: command.state, action: command.on ? 'fan-on' : 'fan-off'
    });
  }
  ingest(topic: string, payload: string, retained = false): Promise<boolean> {
    if (!fridgeIds.includes(topic as FridgeId) || retained) return Promise.resolve(false);
    let reading: Reading;
    try {
      const { temperature, humidity, sampleId } = telemetrySchema.parse(JSON.parse(payload));
      const fridge = topic as FridgeId;
      reading = { _id: `${fridge}:${sampleId}`, fridge, temperature, humidity, receivedAt: this.iso() };
    } catch (error) { return Promise.reject(error); }
    // Only one active write and one newest sample per device; no promise chain per packet.
    this.ingress.set(reading.fridge, reading);
    if (!this.telemetryWork) {
      this.telemetryWork = this.drainTelemetry();
    }
    return this.telemetryWork;
  }
  private async drainTelemetry() {
    let saved = false;
    try {
      while (this.ingress.size) {
        const reading = this.ingress.values().next().value!;
        this.ingress.delete(reading.fridge);
        if (!await this.repo.saveReading(reading)) continue;
        saved = true;
        const previous = this.readings.get(reading.fridge);
        if (!previous || reading.receivedAt >= previous.receivedAt) this.readings.set(reading.fridge, reading);
        await this.serial(() => this.evaluate(reading.fridge));
      }
      return saved;
    } finally { this.telemetryWork = null; }
  }
  setThreshold(fridge: FridgeId, threshold: number, actor: Actor = localActor) {
    return this.serial(async () => {
      if (!Number.isFinite(threshold) || threshold < 0 || threshold > 50) throw new Error('Threshold must be 0–50 °C');
      await this.repo.setThreshold(fridge, threshold);
      this.thresholds.set(fridge, threshold);
      await this.notify('alert', `${fridge}: threshold set to ${threshold} °C.`, fridge,
        { actor, action: 'threshold', outcome: 'saved' });
      await this.evaluate(fridge);
    });
  }
  private async evaluate(fridge: FridgeId) {
    const reading = this.readings.get(fridge);
    const threshold = this.thresholds.get(fridge)!;
    if (!reading || !this.fresh(reading) || reading.temperature <= threshold) return;
    const previous = this.alerts.get(fridge);
    if (previous && this.now() - Date.parse(previous.createdAt) < this.options.cooldownSeconds * 1000) return;
    if (previous && !previous.decision && Date.parse(previous.expiresAt) > this.now()) return;
    const alert: Alert = { _id: randomUUID(), fridge, temperature: reading.temperature, threshold,
      recipient: this.options.recipient.trim().toLowerCase(), createdAt: this.iso(),
      expiresAt: new Date(this.now() + this.options.replyExpirySeconds * 1000).toISOString(), emailState: 'pending' };
    await this.repo.saveAlert(alert);
    this.alerts.set(fridge, alert);
    await this.notify('alert', `${fridge}: ${reading.temperature} °C exceeds ${threshold} °C. Email alert queued. Fan unchanged.`, fridge);
    this.scheduleMail(alert);
  }
  private scheduleMail(alert: Alert) {
    if (this.mailWork.has(alert.fridge)) return;
    const work = this.sendAlert(alert).catch(() => {
      console.warn('Alert delivery state could not be saved; check database availability.');
    }).finally(() => { this.mailWork.delete(alert.fridge); });
    this.mailWork.set(alert.fridge, work);
  }
  async flushEmail() { await Promise.all(this.mailWork.values()); }
  private async sendAlert(alert: Alert) {
    const reading = this.readings.get(alert.fridge);
    if (alert.emailState === 'sent' || alert.decision || Date.parse(alert.expiresAt) <= this.now()
      || !reading || !this.fresh(reading) || reading.temperature <= this.thresholds.get(alert.fridge)!) return;
    // Retries describe the current reading and threshold, not the original failed email.
    alert.temperature = reading.temperature;
    alert.threshold = this.thresholds.get(alert.fridge)!;
    alert.lastAttemptAt = this.iso();
    await this.repo.saveAlert(alert);
    try { await this.mailer.send({ ...alert }); }
    catch {
      await this.notify('error', `${alert.fridge}: email delivery failed or email is not configured. Will retry; fan remains unchanged.`, alert.fridge);
      return;
    }
    alert.emailState = 'sent';
    await this.repo.saveAlert(alert);
    await this.notify('email', `${alert.fridge}: email sent at ${alert.temperature} °C. Reply YES to request the fan; any other reply leaves it unchanged.`, alert.fridge);
  }
  async tick() {
    await this.serial(async () => {
      for (const command of [...this.commands.values()]) {
        if (Date.parse(command.expiresAt) <= this.now()) {
          command.state = 'expired';
          await this.repo.updateCommand(command);
          this.commands.delete(command.commandId);
          await this.commandNotice(command, 'Fan command timed out. No matching Raspberry Pi acknowledgement was received.');
        }
      }
      for (const fridge of fridgeIds) {
        const alert = this.alerts.get(fridge);
        if (alert && (!alert.lastAttemptAt || this.now() - Date.parse(alert.lastAttemptAt) >= this.options.emailRetrySeconds * 1000))
          this.scheduleMail(alert);
        await this.evaluate(fridge);
      }
    });
    for (const command of this.commands.values()) {
      if (command.state === 'queued') void this.dispatch(command).catch(() => {
        console.warn('Fan command state could not be saved; delivery remains unconfirmed.');
      });
    }
  }
  acceptFanStatus(payload: string) {
    return this.serial(async () => {
      const status = fanStatusSchema.parse(JSON.parse(payload));
      const age = this.now() - Date.parse(status.timestamp);
      if (status.online && (age < -5000 || age > 30000)) return;
      this.fan = status;
      this.fanReceivedAt = this.now();
      const command = status.commandId ? this.commands.get(status.commandId) : undefined;
      if (command && status.online && status.on === command.on && command.state !== 'queued') {
        command.state = 'confirmed';
        await this.repo.updateCommand(command);
        this.commands.delete(command.commandId);
        await this.commandNotice(command, `Raspberry Pi confirmed fan ${status.on ? 'ON' : 'OFF'}${status.simulated ? ' (SIMULATION)' : ''}.`);
      }
    });
  }
  private fanOnline() {
    return this.brokerConnected && !!this.fan?.online && this.now() - this.fanReceivedAt <= 30000;
  }
  private newCommand(on: boolean, actor: Actor, alert?: Alert): CommandRecord {
    return { commandId: randomUUID(), on, expiresAt: new Date(Math.min(this.now() + 15000,
      alert ? Date.parse(alert.expiresAt) : Infinity)).toISOString(),
      createdAt: this.iso(), actor, state: 'queued', ...(alert ? { approvalId: alert._id, fridge: alert.fridge } : {}) };
  }
  private async dispatch(command: CommandRecord) {
    if (command.state !== 'queued' || (command.on ? !this.fanOnline() : !this.brokerConnected)) return;
    if (command.approvalId) {
      const reading = this.readings.get(command.fridge!);
      if (!reading || !this.fresh(reading) || reading.temperature <= this.thresholds.get(command.fridge!)!) {
        command.state = 'cancelled';
        await this.repo.updateCommand(command);
        this.commands.delete(command.commandId);
        await this.commandNotice(command, 'Approval cancelled: temperature recovered or reading is stale.');
        return;
      }
    }
    command.state = 'dispatching';
    await this.repo.updateCommand(command); // Durable intent precedes any MQTT write.
    if (command.state !== 'dispatching') return; // OFF may have preempted while saving.
    const latest = command.fridge ? this.readings.get(command.fridge) : undefined;
    if (Date.parse(command.expiresAt) <= this.now()
      || (command.approvalId && (!latest || !this.fresh(latest) || latest.temperature <= this.thresholds.get(command.fridge!)!))) {
      command.state = Date.parse(command.expiresAt) <= this.now() ? 'expired' : 'cancelled';
      await this.repo.updateCommand(command);
      this.commands.delete(command.commandId);
      await this.commandNotice(command, 'Fan command cancelled before publication: approval or reading no longer current.');
      return;
    }
    try {
      await this.publisher.publish({ commandId: command.commandId, on: command.on, expiresAt: command.expiresAt });
      if (command.state === 'dispatching') command.state = 'pending';
    } catch {
      // Publication failure is ambiguous. Never retry a possibly delivered ON.
      if (command.state === 'dispatching') command.state = 'uncertain';
    }
    await this.repo.updateCommand(command);
    await this.commandNotice(command);
  }
  async requestFan(on: boolean, actor: Actor = localActor) {
    const command = await this.serial(async () => {
      if (on && !this.fanOnline()) throw new Error('Fan controller is offline or stale. Check the Raspberry Pi.');
      if (on && this.commands.size) throw new Error('A fan command is already awaiting confirmation.');
      const existingOff = !on ? [...this.commands.values()].find(c => !c.on) : undefined;
      if (existingOff) {
        // OFF is safe to retry with the same identity, unlike an ambiguous ON.
        existingOff.state = 'queued';
        existingOff.expiresAt = new Date(this.now() + 15000).toISOString();
        await this.repo.updateCommand(existingOff);
        await this.notify('fan', 'OFF requested again; original command still awaits confirmation.', undefined,
          { actor, commandId: existingOff.commandId, action: 'fan-off', outcome: 'coalesced' });
        return existingOff;
      }
      const next = this.newCommand(on, actor);
      await this.repo.createCommand(next);
      if (!on) await this.preemptOn();
      this.commands.set(next.commandId, next);
      await this.commandNotice(next, `Fan ${on ? 'ON' : 'OFF'} queued; awaiting Raspberry Pi confirmation.`);
      return next;
    });
    await this.dispatch(command); // Network waits never hold the control queue.
    return { commandId: command.commandId, on: command.on, expiresAt: command.expiresAt };
  }
  private async preemptOn() {
    for (const command of [...this.commands.values()]) {
      if (!command.on) continue;
      command.state = 'superseded';
      await this.repo.updateCommand(command);
      this.commands.delete(command.commandId);
      await this.commandNotice(command, 'Pending ON superseded by OFF.');
    }
  }
  async reply(sender: string, subject: string, text: string) {
    const result = await this.serial(async () => {
      const token = subject.match(/\[Northstar:([a-f0-9-]{36})\]/i)?.[1];
      const alert = token ? await this.repo.alert(token) : null;
      if (!alert || (alert.emailState !== 'sent' && !alert.lastAttemptAt) || alert.decision || Date.parse(alert.expiresAt) <= this.now()
        || sender.trim().toLowerCase() !== alert.recipient
        || alert.recipient !== this.options.recipient.trim().toLowerCase()) return null;
      const answer = text.trimStart().split(/\r?\n/)[0].trim().toUpperCase();
      if (answer !== 'YES' && answer !== 'NO') return null;
      const reading = this.readings.get(alert.fridge);
      const allowed = answer === 'YES' && reading && this.fresh(reading)
        && reading.temperature > this.thresholds.get(alert.fridge)! && this.commands.size === 0;
      const actor: Actor = { id: `mailbox:${createHash('sha256').update(alert.recipient).digest('hex')}` };
      const command = allowed ? this.newCommand(true, actor, alert) : undefined;
      const decision = command ? 'YES' : 'NO';
      if (!await this.repo.consumeAlert(alert._id, decision, this.iso(), command)) return null;
      Object.assign(alert, { decision, decidedAt: this.iso(), command });
      if (this.alerts.get(alert.fridge)?._id === alert._id) this.alerts.set(alert.fridge, alert);
      if (command) {
        this.commands.set(command.commandId, command);
        await this.commandNotice(command, 'Email approval consumed; fan command queued, not yet confirmed.');
      }
      await this.notify('email', `${alert.fridge}: reply processed (${decision}).${decision === 'NO' ? ' No fan action taken.' : ''}`, alert.fridge,
        { actor, commandId: command?.commandId, action: 'approval', outcome: decision });
      return { command };
    });
    if (!result) return 'ignored';
    if (result.command) await this.dispatch(result.command);
    return 'processed';
  }
  async snapshot() {
    await this.queue;
    const pending = [...this.commands.values()].at(-1);
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
        simulated: this.fan?.simulated ?? false, pending: pending?.on ?? null,
        commandId: pending?.commandId ?? null, outcome: pending?.state ?? null },
      notifications: await this.repo.notifications()
    };
  }
}
