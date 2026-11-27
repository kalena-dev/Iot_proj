import { Monitor } from '../src/monitor.js';
import { fridgeIds, type Alert, type CommandRecord, type MailboxCursor, type MailboxFailure, type FanCommand, type FridgeId, type Notification, type Reading, type Repository, type Setting } from '../src/model.js';

export class MemoryRepository implements Repository {
  config = new Map<FridgeId, number>();
  readings: Reading[] = [];
  alerts: Alert[] = [];
  notices: Notification[] = [];
  commands = new Map<string, CommandRecord>();
  cursors = new Map<string, MailboxCursor>();
  failures = new Map<string, MailboxFailure>();
  async initialize(defaultThreshold: number) {
    for (const id of fridgeIds) if (!this.config.has(id)) this.config.set(id, defaultThreshold);
  }
  async settings(): Promise<Setting[]> { return [...this.config].map(([_id, threshold]) => ({ _id, threshold })); }
  async setThreshold(id: FridgeId, threshold: number) { this.config.set(id, threshold); }
  async saveReading(reading: Reading) {
    if (this.readings.some(r => r._id === reading._id)) return false;
    this.readings.push(structuredClone(reading)); return true;
  }
  async latestReading(id: FridgeId) { return structuredClone(this.readings.filter(r => r.fridge === id).at(-1) ?? null); }
  async latestAlert(id: FridgeId) { return structuredClone(this.alerts.filter(a => a.fridge === id).at(-1) ?? null); }
  async saveAlert(alert: Alert) {
    const index = this.alerts.findIndex(a => a._id === alert._id);
    if (index < 0) this.alerts.push(structuredClone(alert));
    else {
      const { decision: _decision, command: _command, decidedAt: _decidedAt, ...fields } = structuredClone(alert);
      Object.assign(this.alerts[index], fields);
    }
  }
  async alert(id: string) { return structuredClone(this.alerts.find(a => a._id === id) ?? null); }
  async consumeAlert(id: string, decision: 'YES' | 'NO', decidedAt: string, command?: CommandRecord) {
    const alert = this.alerts.find(a => a._id === id);
    if (!alert || alert.decision) return false;
    Object.assign(alert, { decision, decidedAt, ...(command ? { command: structuredClone(command) } : {}) });
    return true;
  }
  async createCommand(command: CommandRecord) { this.commands.set(command.commandId, structuredClone(command)); }
  async updateCommand(command: CommandRecord) {
    if (command.approvalId) this.alerts.find(a => a._id === command.approvalId)!.command = structuredClone(command);
    else this.commands.set(command.commandId, structuredClone(command));
  }
  async outstandingCommands() {
    return structuredClone([...this.commands.values(), ...this.alerts.flatMap(a => a.command ? [a.command] : [])]
      .filter(c => ['queued', 'dispatching', 'pending', 'uncertain'].includes(c.state)));
  }
  async mailboxCursor(id: string) { return structuredClone(this.cursors.get(id) ?? null); }
  async saveMailboxCursor(cursor: MailboxCursor) { this.cursors.set(cursor._id, structuredClone(cursor)); }
  async mailboxFailures(mailbox: string, uidValidity: string) {
    return structuredClone([...this.failures.values()].filter(f => f.mailbox === mailbox && f.uidValidity === uidValidity)
      .sort((a, b) => (a.attempts ?? 0) - (b.attempts ?? 0) || a.uid - b.uid).slice(0, 25));
  }
  async saveMailboxFailure(failure: MailboxFailure) {
    this.failures.set(failure._id, structuredClone({ ...failure, attempts: (this.failures.get(failure._id)?.attempts ?? 0) + 1 }));
  }
  async clearMailboxFailure(id: string) { this.failures.delete(id); }
  async notification(notification: Notification) {
    if (!this.notices.some(n => n._id === notification._id)) this.notices.push(structuredClone(notification));
  }
  async notifications() { return [...this.notices].reverse().slice(0, 30); }
}
export async function fixture(repo = new MemoryRepository(), hooks: {
  send?: (alert: Alert) => Promise<void>; publish?: (command: FanCommand) => Promise<void>
} = {}) {
  let now = Date.parse('2025-10-15T12:00:00Z');
  let emailFails = false;
  let publishFails = false;
  const emails: Alert[] = [];
  const commands: FanCommand[] = [];
  const monitor = new Monitor(repo, { async send(alert) {
    if (emailFails) throw new Error('SMTP unavailable');
    if (hooks.send) await hooks.send(alert);
    emails.push(structuredClone(alert));
  } }, { async publish(command) {
    if (publishFails) throw new Error('Broker unavailable');
    if (hooks.publish) await hooks.publish(command);
    commands.push(command);
  } }, { defaultThreshold: 8, staleSeconds: 30, cooldownSeconds: 900,
    replyExpirySeconds: 900, emailRetrySeconds: 60, recipient: 'owner@example.com' }, () => now);
  await monitor.initialize();
  monitor.brokerConnected = true;
  const online = async (on = false, commandId: string | null = null) => monitor.acceptFanStatus(JSON.stringify({
    online: true, on, simulated: false, commandId, timestamp: new Date(now).toISOString()
  }));
  await online();
  const ingest = async (topic: string, payload: string, retained = false) => {
    const saved = await monitor.ingest(topic, payload, retained);
    await monitor.flushEmail();
    return saved;
  };
  return { monitor, repo, emails, commands, online, ingest, advance: (seconds: number) => { now += seconds * 1000; },
    failEmail: (fail: boolean) => { emailFails = fail; }, failPublish: (fail: boolean) => { publishFails = fail; },
    subject: () => `Re: Frig1 temperature alert [Northstar:${emails.at(-1)!._id}]` };
}
