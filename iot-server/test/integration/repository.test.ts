import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { MongoClient } from 'mongodb';
import { MongoRepository } from '../../src/repository.js';
import type { Alert, CommandRecord } from '../../src/model.js';

const uri = process.env.SMARTSTORE_IOT_TEST_URI;

test('isolated Mongo: approval CAS, durable command outcomes and fair failed-UID retries', { skip: !uri, timeout: 15000 }, async () => {
  const url = new URL(uri!);
  assert.equal(url.protocol, 'mongodb:');
  assert.ok(['localhost', '127.0.0.1', '[::1]'].includes(url.hostname), 'Only an isolated loopback MongoDB is permitted');
  assert.equal(url.username, '', 'Do not pass production credentials to integration tests');
  const client = new MongoClient(uri!, { serverSelectionTimeoutMS: 2000 });
  const database = `northstar_iot_test_${randomUUID().replaceAll('-', '')}`;
  const repo = new MongoRepository(client, database);
  try {
    await repo.initialize(8);
    const now = Date.now();
    const alert: Alert = { _id: randomUUID(), fridge: 'Frig1', temperature: 22, threshold: 8,
      recipient: 'synthetic@local.test', createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + 900000).toISOString(), emailState: 'sent' };
    await repo.saveAlert(alert);
    const command: CommandRecord = { commandId: randomUUID(), on: true, createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + 15000).toISOString(), actor: { id: 'synthetic-operator' },
      approvalId: alert._id, fridge: 'Frig1', state: 'queued' };
    const results = await Promise.all(Array.from({ length: 8 }, () => repo.consumeAlert(alert._id, 'YES', command.createdAt, command)));
    assert.equal(results.filter(Boolean).length, 1);
    assert.equal((await repo.alert(alert._id))?.command?.commandId, command.commandId);
    // A concurrent SMTP completion cannot overwrite the consumed approval/command.
    await repo.saveAlert({ ...alert, emailState: 'sent' });
    assert.equal((await repo.alert(alert._id))?.decision, 'YES');
    assert.equal((await repo.outstandingCommands()).length, 1);
    command.state = 'confirmed';
    await repo.updateCommand(command);
    assert.equal((await repo.outstandingCommands()).length, 0);
    assert.equal((await repo.alert(alert._id))?.command?.state, 'confirmed');
    await repo.saveMailboxCursor({ _id: 'synthetic-mailbox', uidValidity: '1', lastUid: 100 });
    assert.equal((await repo.mailboxCursor('synthetic-mailbox'))?.lastUid, 100);
    await Promise.all(Array.from({ length: 30 }, (_, uid) => repo.saveMailboxFailure({
      _id: `synthetic-${uid}`, mailbox: 'synthetic-mailbox', uidValidity: '1', uid
    })));
    const first = await repo.mailboxFailures('synthetic-mailbox', '1');
    assert.equal(first.length, 25);
    await Promise.all(first.map(failure => repo.saveMailboxFailure(failure)));
    const next = await repo.mailboxFailures('synthetic-mailbox', '1');
    assert.deepEqual(next.slice(0, 5).map(f => f.uid), [25, 26, 27, 28, 29]);
    await repo.clearMailboxFailure('synthetic-25');
    assert.ok(!(await repo.mailboxFailures('synthetic-mailbox', '1')).some(f => f.uid === 25));
  } finally {
    await client.db(database).dropDatabase();
    await client.close();
  }
});
