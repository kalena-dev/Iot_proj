import { Monitor } from '../src/monitor.js';
import { fridgeIds, type Alert, type FanCommand, type FridgeId, type Notification, type Reading, type Repository, type Setting } from '../src/model.js';

export class MemoryRepository implements Repository {
  config = new Map<FridgeId, number>();
  readings: Reading[] = [];
  alerts: Alert[] = [];
  notices: Notification[] = [];
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
    else this.alerts[index] = structuredClone(alert);
  }
  async notification(notification: Notification) { this.notices.push(notification); }
  async notifications() { return [...this.notices].reverse().slice(0, 30); }
}
export async function fixture(repo = new MemoryRepository()) {
  let now = Date.parse('2025-10-15T12:00:00Z');
  let emailFails = false;
  let publishFails = false;
  const emails: Alert[] = [];
  const commands: FanCommand[] = [];
  const monitor = new Monitor(repo, { async send(alert) {
    if (emailFails) throw new Error('SMTP unavailable');
    emails.push(structuredClone(alert));
  } }, { async publish(command) {
    if (publishFails) throw new Error('Broker unavailable');
    commands.push(command);
  } }, { defaultThreshold: 8, staleSeconds: 30, cooldownSeconds: 900,
    replyExpirySeconds: 900, emailRetrySeconds: 60, recipient: 'owner@example.com' }, () => now);
  await monitor.initialize();
  monitor.brokerConnected = true;
  const online = async (on = false, commandId: string | null = null) => monitor.acceptFanStatus(JSON.stringify({
    online: true, on, simulated: false, commandId, timestamp: new Date(now).toISOString()
  }));
  await online();
  return { monitor, repo, emails, commands, online, advance: (seconds: number) => { now += seconds * 1000; },
    failEmail: (fail: boolean) => { emailFails = fail; }, failPublish: (fail: boolean) => { publishFails = fail; },
    subject: () => `Re: Frig1 temperature alert [Northstar:${emails.at(-1)!._id}]` };
}
