const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const settle = () => new Promise(resolve => setImmediate(resolve));
const load = (name, context) => vm.runInNewContext(fs.readFileSync(`src/SmartStore/wwwroot/js/${name}.js`, 'utf8'), context);

function element() {
  const classes = new Set();
  return { dataset: {}, textContent: '', hidden: false, disabled: false, handlers: {},
    classList: { toggle(name, enabled) { enabled ? classes.add(name) : classes.delete(name); },
      remove(name) { classes.delete(name); }, contains(name) { return classes.has(name); } },
    addEventListener(name, handler) { this.handlers[name] = handler; },
    replaceChildren() {}, append() {} };
}

async function sensor(readings) {
  let now = Date.UTC(2026, 10, 27, 12);
  let next;
  const status = element();
  const history = element();
  const cards = new Map(['temperature', 'humidity', 'illuminance', 'motion'].map(key => {
    const card = element();
    card.dataset.sensorCard = key;
    card.value = element();
    card.meta = element();
    card.querySelector = selector => selector === '.sensor-value' ? card.value : card.meta;
    return [key, card];
  }));
  const dashboard = element();
  dashboard.dataset.sensorUrl = '/Sensor?handler=Readings';
  dashboard.querySelectorAll = () => [...cards.values()];
  dashboard.querySelector = () => element();
  load('sensor', { document: { querySelector: selector => selector === '[data-sensor-url]' ? dashboard
    : selector === '#sensor-status' ? status : history },
    Intl, Date: { now: () => now }, Number, performance: { now: () => 0 },
    fetch: async () => ({ json: async () => readings }),
    setTimeout: callback => { next = callback; } });
  await settle();
  return { cards, status, history, now, async update(value, advance = 0) {
    readings = value;
    now += advance;
    await next();
    await settle();
  } };
}

test('Minew readings with missing, seconds, fractional, or future timestamps are never live', async () => {
  const ui = await sensor({ temperature: 21, motion: true });
  for (const timestamp of [undefined, 1795780800, 1795780800000.5, ui.now + 60000]) {
    await ui.update({ temperature: 21, motion: true, timestamp });
    assert.equal(ui.status.dataset.state, 'waiting');
    assert.equal(ui.cards.get('temperature').dataset.state, 'unknown');
    assert.equal(ui.cards.get('motion').dataset.state, 'unknown');
    assert.match(ui.cards.get('motion').meta.textContent, /unavailable/);
    assert.doesNotMatch(ui.history.textContent, /Last motion observed/);
  }
});

test('Minew fresh motion becomes visibly stale after disconnection', async () => {
  const now = Date.UTC(2026, 10, 27, 12);
  const ui = await sensor({ temperature: 21, motion: true, timestamp: now });
  assert.equal(ui.status.dataset.state, 'live');
  assert.equal(ui.cards.get('motion').dataset.state, 'active');
  assert.match(ui.history.textContent, /Last motion observed/);
  await ui.update({ status: 'Pareto Anywhere is unavailable.' }, 31000);
  assert.equal(ui.status.dataset.state, 'waiting');
  assert.equal(ui.cards.get('motion').dataset.state, 'stale');
  assert.match(ui.cards.get('temperature').meta.textContent, /Stale/);
});

function budget() {
  const panel = element();
  panel.dataset.spent = '123.45';
  const input = element();
  input.value = '';
  input.validity = { valid: true };
  Object.defineProperty(input, 'valueAsNumber', { get: () => input.value === '' ? NaN : Number(input.value) });
  const message = element();
  const progress = element();
  panel.querySelector = selector => selector === 'input' ? input
    : selector === 'progress' ? progress : message;
  const storage = new Map();
  const lifecycle = {};
  load('spending', { document: { querySelector: () => panel }, Intl,
    window: { addEventListener(name, handler) { lifecycle[name] = handler; } },
    localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) } });
  return { panel, input, message, progress, storage, lifecycle };
}

test('budget calculations work without persisting private targets', () => {
  const ui = budget();
  ui.input.value = '200';
  ui.input.handlers.input();
  assert.equal(ui.message.textContent, '$76.55 left in budget');
  assert.equal(ui.progress.hidden, false);
  assert.equal(ui.storage.size, 0);
  ui.input.value = '100';
  ui.input.handlers.input();
  assert.equal(ui.panel.classList.contains('spending-budget--over'), true);
  ui.input.value = '';
  ui.input.handlers.input();
  assert.equal(ui.progress.hidden, true);
  assert.equal(ui.panel.classList.contains('spending-budget--over'), false);
  ui.input.value = '200';
  ui.input.handlers.input();
  ui.lifecycle.pagehide();
  assert.equal(ui.input.value, '');
  assert.equal(ui.progress.hidden, true);
  assert.equal(budget().input.value, '');
});

async function refrigeration() {
  const nodes = new Map();
  const node = id => { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); };
  const on = Object.assign(element(), { name: 'on', value: 'true' });
  const off = Object.assign(element(), { name: 'on', value: 'false' });
  const form = Object.assign(element(), { id: 'fan-form', action: '/Refrigerators?handler=Fan',
    querySelectorAll: () => [on, off], querySelector: () => null });
  const posts = [];
  let finishOn;
  const status = { timestamp: '2026-11-27T12:00:00Z', brokerConnected: true, emailConnected: true,
    refrigerators: [], notifications: [], fan: { online: true, on: false, simulated: true, pending: null } };
  const document = { getElementById: node, createElement: element,
    querySelectorAll: selector => selector === '#fan-form button' ? [on, off]
      : selector === '.threshold-form, #fan-form' ? [form] : [] };
  class FormData {
    constructor() { this.values = new Map(); }
    set(key, value) { this.values.set(key, value); }
    get(key) { return this.values.get(key); }
  }
  load('refrigerators', { document, FormData, Date, AbortSignal,
    setInterval() {}, fetch: async (_url, options = {}) => {
      if (options.method === 'POST') {
        posts.push(options.body.get('on'));
        if (options.body.get('on') === 'true') await new Promise(resolve => { finishOn = resolve; });
        return { ok: true, json: async () => ({ message: 'Command accepted' }) };
      }
      return { ok: true, json: async () => status };
    } });
  await settle();
  return { on, off, posts, status, finishOn: () => finishOn(),
    submit: button => form.handlers.submit({ preventDefault() {}, submitter: button }) };
}

test('dashboard OFF can preempt an unacknowledged ON request', async () => {
  const ui = await refrigeration();
  const starting = ui.submit(ui.on);
  await settle();
  assert.equal(ui.on.disabled, true);
  assert.equal(ui.off.disabled, false);
  await ui.submit(ui.off);
  assert.deepEqual(ui.posts, ['true', 'false']);
  ui.finishOn();
  await starting;
});
