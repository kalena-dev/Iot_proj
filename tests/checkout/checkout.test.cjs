const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

function setup(mode = 'simulation', tags = []) {
  class Element {
    constructor(dataset = {}) { this.dataset = dataset; this.children = []; this.handlers = {}; this.value = ''; }
    addEventListener(name, fn) { this.handlers[name] = fn; }
    replaceChildren() { this.children = []; }
    append(...children) { this.children.push(...children); }
    focus() {}
    click() { this.handlers.click(); }
  }
  const selectors = Object.fromEntries(['#item-code', '#scan-error', '#cart-list', '#cart-total', '#checkout-button', '#rfid-scan', '#rfid-status', '#Cart', '#add-item', '.rfid-scanner'].map(s => [s, new Element()]));
  selectors['.rfid-scanner'].dataset = {mode, session: 'private-session'};
  const products = ['1001', '1002'].map(code => new Element({code, name: code, price: '1.00', stock: '3'}));
  let nextPoll;
  const document = { querySelector: s => selectors[s], querySelectorAll: () => products, createElement: () => new Element() };
  const context = {document, Intl, URL, URLSearchParams, window: {location: {href: 'https://store/Checkout'}, setTimeout: fn => {nextPoll = fn;}}, fetch: async (url, options) => {
    assert.equal(options.headers['X-Rfid-Session'], 'private-session');
    assert.equal(url.searchParams.has('session'), false);
    return {ok: true, status: 200, json: async () => tags};
  }};
  vm.runInNewContext(fs.readFileSync('src/SmartStore/wwwroot/js/checkout.js', 'utf8'), context);
  return {selectors, cart: () => JSON.parse(selectors['#Cart'].value), poll: () => nextPoll()};
}

test('manual input retains stock limits and removals', () => {
  const ui = setup();
  for (let i = 0; i < 4; i++) {
    ui.selectors['#item-code'].value = '1001';
    ui.selectors['#add-item'].click();
  }
  assert.deepEqual(ui.cart(), {'1001': 3});
  assert.match(ui.selectors['#scan-error'].textContent, /out of stock/);
  ui.selectors['#cart-list'].children[0].children[1].click();
  assert.deepEqual(ui.cart(), {'1001': 2});
});

test('explicit simulation supports multiple tags and ignores repeat scans', () => {
  const ui = setup();
  ui.selectors['#rfid-scan'].click();
  ui.selectors['#rfid-scan'].click();
  assert.deepEqual(ui.cart(), {'1001': 2, '1002': 1});
});

test('reader polling deduplicates physical tags and does not re-add removed tags', async () => {
  const ui = setup('reader', [{epc: 'AABB', code: '1001'}, {epc: 'CCDD', code: '1001'}]);
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(ui.cart(), {'1001': 2});
  ui.selectors['#cart-list'].children[0].children[1].click();
  await ui.poll();
  assert.deepEqual(ui.cart(), {'1001': 1});
});
