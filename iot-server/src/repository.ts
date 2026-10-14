import { MongoClient, MongoServerError, type Db } from 'mongodb';
import { fridgeIds, type Alert, type FridgeId, type Notification, type Reading, type Repository, type Setting } from './model.js';

export class MongoRepository implements Repository {
  private readonly db: Db;
  constructor(private readonly client: MongoClient, database: string) { this.db = client.db(database); }
  async initialize(defaultThreshold: number) {
    await this.client.connect();
    await this.db.collection<Reading>('fridge_readings').createIndex({ fridge: 1, receivedAt: -1 });
    // Keep 30 days of telemetry; settings and alerts are not expired.
    await this.db.collection('fridge_readings').createIndex({ expireAt: 1 }, { expireAfterSeconds: 0 });
    await this.db.collection<Alert>('fridge_alerts').createIndex({ fridge: 1, createdAt: -1 });
    await this.db.collection<Notification>('iot_notifications').createIndex({ createdAt: -1 });
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
    await this.db.collection<Alert>('fridge_alerts').replaceOne({ _id: alert._id }, alert, { upsert: true });
  }
  async notification(notification: Notification) {
    await this.db.collection<Notification>('iot_notifications').insertOne(notification);
  }
  async notifications() {
    return this.db.collection<Notification>('iot_notifications').find().sort({ createdAt: -1 }).limit(30).toArray();
  }
}
