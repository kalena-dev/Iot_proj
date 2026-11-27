import { MongoClient, MongoServerError, type Db } from 'mongodb';
import { fridgeIds, type Alert, type CommandRecord, type MailboxCursor, type MailboxFailure, type FridgeId, type Notification, type Reading, type Repository, type Setting } from './model.js';

export class MongoRepository implements Repository {
  private readonly db: Db;
  constructor(private readonly client: MongoClient, database: string) {
    this.db = client.db(database, { writeConcern: { w: 'majority', j: true } });
  }
  async initialize(defaultThreshold: number) {
    await this.client.connect();
    await this.db.collection<Reading>('fridge_readings').createIndex({ fridge: 1, receivedAt: -1 });
    // Keep 30 days of telemetry; settings and alerts are not expired.
    await this.db.collection('fridge_readings').createIndex({ expireAt: 1 }, { expireAfterSeconds: 0 });
    await this.db.collection<Alert>('fridge_alerts').createIndex({ fridge: 1, createdAt: -1 });
    await this.db.collection<Notification>('iot_notifications').createIndex({ createdAt: -1 });
    await this.db.collection<Alert>('fridge_alerts').createIndex({ 'command.state': 1 });
    await this.db.collection('iot_commands').createIndex({ state: 1 });
    await this.db.collection('iot_mailbox_failures').createIndex({ mailbox: 1, uidValidity: 1, attempts: 1, uid: 1 });
    // Remove previously logged bearer material as well as redacting new submissions.
    await this.db.collection('email_log').updateMany(
      { $or: [{ Kind: 'temperature-alert' }, { Subject: /\[Northstar:/i }] },
      { $set: { Subject: 'Temperature alert [approval redacted]' }, $unset: { Reference: '' } });
    for (const _id of fridgeIds) {
      await this.db.collection<Setting>('fridge_settings').updateOne(
        { _id }, { $setOnInsert: { threshold: defaultThreshold } }, { upsert: true });
    }
  }
  async settings() { return this.db.collection<Setting>('fridge_settings').find().toArray(); }
  async setThreshold(_id: FridgeId, threshold: number) {
    await this.db.collection<Setting>('fridge_settings').updateOne({ _id }, { $set: { threshold } });
  }
  async saveReading(reading: Reading) {
    try {
      await this.db.collection<Reading & { expireAt: Date }>('fridge_readings').insertOne({
        ...reading, expireAt: new Date(Date.parse(reading.receivedAt) + 30 * 86400_000)
      });
      return true;
    } catch (error) {
      if (error instanceof MongoServerError && error.code === 11000) return false;
      throw error;
    }
  }
  async latestReading(fridge: FridgeId) {
    return this.db.collection<Reading>('fridge_readings').findOne({ fridge }, { sort: { receivedAt: -1 } });
  }
  async latestAlert(fridge: FridgeId) {
    return this.db.collection<Alert>('fridge_alerts').findOne({ fridge }, { sort: { createdAt: -1 } });
  }
  async saveAlert(alert: Alert) {
    // Mail delivery runs independently of approvals: never overwrite a consumed decision.
    const { decision: _decision, decidedAt: _decidedAt, command: _command, _id, ...fields } = alert;
    await this.db.collection<Alert>('fridge_alerts').updateOne({ _id }, { $set: fields }, { upsert: true });
  }
  async alert(_id: string) { return this.db.collection<Alert>('fridge_alerts').findOne({ _id }); }
  async consumeAlert(_id: string, decision: 'YES' | 'NO', decidedAt: string, command?: CommandRecord) {
    const result = await this.db.collection<Alert>('fridge_alerts').updateOne(
      { _id, decision: { $exists: false } }, { $set: { decision, decidedAt, ...(command ? { command } : {}) } });
    return result.modifiedCount === 1;
  }
  async createCommand(command: CommandRecord) {
    await this.db.collection<CommandRecord & { _id: string }>('iot_commands').insertOne({ _id: command.commandId, ...command });
  }
  async updateCommand(command: CommandRecord) {
    if (command.approvalId) {
      await this.db.collection<Alert>('fridge_alerts').updateOne(
        { _id: command.approvalId, 'command.commandId': command.commandId }, { $set: { command } });
    } else {
      await this.db.collection<CommandRecord & { _id: string }>('iot_commands').updateOne(
        { _id: command.commandId }, { $set: command });
    }
  }
  async outstandingCommands() {
    const states: CommandRecord['state'][] = ['queued', 'dispatching', 'pending', 'uncertain'];
    const [manual, approvals] = await Promise.all([
      this.db.collection<CommandRecord & { _id: string }>('iot_commands').find({ state: { $in: states } }).toArray(),
      this.db.collection<Alert>('fridge_alerts').find({ 'command.state': { $in: states } }).toArray()
    ]);
    return [...manual, ...approvals.map(a => a.command!)].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  }
  async mailboxCursor(_id: string) { return this.db.collection<MailboxCursor>('iot_mailbox_cursors').findOne({ _id }); }
  async saveMailboxCursor(cursor: MailboxCursor) {
    await this.db.collection<MailboxCursor>('iot_mailbox_cursors').replaceOne({ _id: cursor._id }, cursor, { upsert: true });
  }
  async mailboxFailures(mailbox: string, uidValidity: string) {
    return this.db.collection<MailboxFailure>('iot_mailbox_failures').find({ mailbox, uidValidity }).sort({ attempts: 1, uid: 1 }).limit(25).toArray();
  }
  async saveMailboxFailure(failure: MailboxFailure) {
    const { attempts: _attempts, ...fields } = failure;
    await this.db.collection<MailboxFailure>('iot_mailbox_failures').updateOne(
      { _id: failure._id }, { $set: fields, $inc: { attempts: 1 } }, { upsert: true });
  }
  async clearMailboxFailure(_id: string) {
    await this.db.collection<MailboxFailure>('iot_mailbox_failures').deleteOne({ _id });
  }
  async notification(notification: Notification) {
    await this.db.collection<Notification>('iot_notifications').updateOne(
      { _id: notification._id }, { $setOnInsert: notification }, { upsert: true });
  }
  async notifications() {
    return this.db.collection<Notification>('iot_notifications').find().sort({ createdAt: -1 }).limit(30).toArray();
  }
}
