const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

function setup(mode = 'simulation', tags = [], codes = new Map(), controls = {}) {
  class Element {
    constructor(dataset = {}) { this.dataset = dataset; this.children = []; this.handlers = {}; this.value = ''; }
    addEventListener(name, fn) { this.handlers[name] = fn; }
    replaceChildren() { this.children = []; }
    append(...children) { this.children.push(...children); }
    focus() {}
    setAttribute(name, value) { this[name] = value; }
    click() { return this.handlers.click(); }
  }
  const selectors = Object.fromEntries(['#item-code', '#barcode-match', '#barcode-product', '#scan-error', '#cart-list', '#cart-total', '#checkout-button', '#checkout-form', '#AttemptId', '#pair-reader', '#close-reader', '#rfid-scan', '#rfid-status', '#Cart', '#add-coupon', '#checkout-coupons', '#CouponId', '#coupon-breakdown', '#coupon-label', '#cart-subtotal', '#cart-discount', '.rfid-scanner'].map(s => [s, new Element()]));
  selectors['#barcode-match'].hidden = true;
  selectors['#checkout-coupons'].hidden = true;
  selectors['#CouponId'].selectedOptions = [];
  selectors['[name="__RequestVerificationToken"]'] = new Element();
  selectors['[name="__RequestVerificationToken"]'].value = 'csrf-token';
  for (const code of ['1001', '1002']) codes.set(code, {code, isTag:false});
  codes.set('E2801191A503006599BFD10D', {code:'1002', isTag:true});
  selectors['.rfid-scanner'].dataset = {mode, session: 'private-session'};
  const products = ['1001', '1002'].map(code => new Element({code, name: code, price: '1.00', stock: '3'}));
  const lookups = [];
  const actions = [];
  let nextPoll;
  const details = new Map();
  const document = { getElementById: id => {
    if (!details.has(id)) details.set(id, new Element());
    return details.get(id);
  }, querySelector: s => selectors[s], querySelectorAll: () => products, createElement: () => new Element() };
  const context = {document, Intl, URL, URLSearchParams, window: {location: {href: 'https://store/Checkout'}, setTimeout: fn => {nextPoll = fn;}}, fetch: async (url, options) => {
    if (typeof url === 'string') {
      if (url.includes('PairRfid') || url.includes('CloseRfid')) {
        assert.equal(options.headers['X-Rfid-Session'], 'private-session');
        assert.equal(options.body.get('__RequestVerificationToken'), 'csrf-token');
        actions.push(url);
        return {ok: true, status: 200};
      }
      if (url.includes('AssignCode')) {
        assert.equal(options.body.get('__RequestVerificationToken'), 'csrf-token');
        codes.set(options.body.get('code').toUpperCase(), {code:options.body.get('upc'), isTag:false});
        return {ok:true, json:async () => codes.get(options.body.get('code').toUpperCase())};
      }
      const key = new URL(url, 'https://store/Checkout').searchParams.get('code').toUpperCase();
      lookups.push(key);
      if (controls.lookupGate) await controls.lookupGate(key);
      if (controls.rejectedCodes?.includes(key)) return {ok: false, status: 409};
      return {ok:codes.has(key), status:codes.has(key) ? 200 : 404, json:async () => codes.get(key)};
    }
    assert.equal(options.headers['X-Rfid-Session'], 'private-session');
    assert.equal(url.searchParams.has('session'), false);
    return {ok: true, status: 200, json: async () => tags};
  }};
  vm.runInNewContext(fs.readFileSync('src/SmartStore/wwwroot/js/checkout.js', 'utf8'), context);
  return {selectors, lookups, actions, cart: () => JSON.parse(selectors['#Cart'].value), poll: () => nextPoll()};
}

test('manual input retains stock limits and removals', async () => {
  const ui = setup();
  for (let i = 0; i < 4; i++) {
    ui.selectors['#item-code'].value = '1001';
    await ui.selectors['#item-code'].handlers.keydown({key: 'Enter', preventDefault() {}});
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
  // Scanned tags are sent one by one, so checkout sells those exact units.
  assert.deepEqual(ui.cart(), {'AABB': 1, 'CCDD': 1});
  ui.selectors['#cart-list'].children[0].children[1].click();
  await ui.poll();
  assert.deepEqual(ui.cart(), {'AABB': 1});
});

test('barcode units and scanned tags of one product are sent apart; removal takes a barcode unit first', async () => {
  const ui = setup('reader', [{epc: 'E2801191A503006599BFD10D', code: '1002'}]);
  await new Promise(resolve => setImmediate(resolve));
  ui.selectors['#item-code'].value = '1002';
  await ui.selectors['#item-code'].handlers.keydown({key: 'Enter', preventDefault() {}});
  assert.deepEqual(ui.cart(), {'1002': 1, 'E2801191A503006599BFD10D': 1});
  ui.selectors['#cart-list'].children[0].children[1].click();
  assert.deepEqual(ui.cart(), {'E2801191A503006599BFD10D': 1});
  ui.selectors['#cart-list'].children[0].children[1].click();
  assert.deepEqual(ui.cart(), {});
});

test('the same EPC typed twice is one item, sent as that tag', async () => {
  const ui = setup();
  const input = ui.selectors['#item-code'];
  for (const epc of [' e2801191a503006599bfd10d ', 'E2801191A503006599BFD10D']) {
    input.value = epc;
    await input.handlers.keydown({key: 'Enter', preventDefault() {}});
  }
  assert.deepEqual(ui.cart(), {'E2801191A503006599BFD10D': 1});
  assert.match(ui.selectors['#scan-error'].textContent, /already been scanned/);
});

test('separate reader events with the same EPC count one item', async () => {
  const tags = [{epc: 'AABB', code: '1001', scanId: 'first'}];
  const ui = setup('reader', tags);
  await new Promise(resolve => setImmediate(resolve));
  await ui.poll();
  assert.deepEqual(ui.cart(), {'AABB': 1});
  tags.push({epc: 'AABB', code: '1001', scanId: 'second'});
  await ui.poll();
  await ui.poll();
  assert.deepEqual(ui.cart(), {'AABB': 1});
});

test('an unrecognized barcode of any format can be assigned a product across sessions', async () => {
  const codes = new Map();
  const ui = setup('simulation', [], codes);
  for (const barcode of ['X', '00001/case-XL']) {
    ui.selectors['#item-code'].value = barcode;
    ui.selectors['#item-code'].handlers.input();
    await ui.selectors['#item-code'].handlers.keydown({key: 'Enter', preventDefault() {}});
    assert.equal(ui.selectors['#barcode-match'].hidden, false);
    ui.selectors['#barcode-product'].value = '1001';
    await ui.selectors['#item-code'].handlers.keydown({key: 'Enter', preventDefault() {}});
    assert.equal(ui.selectors['#barcode-match'].hidden, true);
  }
  const next = setup('simulation', [], codes);
  next.selectors['#item-code'].value = 'X';
  await next.selectors['#item-code'].handlers.keydown({key: 'Enter', preventDefault() {}});
  assert.deepEqual(next.cart(), {'1001': 1});
});

test('selecting and removing a loyalty coupon updates the cart totals', () => {
  const {selectors} = setup();
  selectors['#add-coupon'].click();
  assert.equal(selectors['#checkout-coupons'].hidden, false);
  selectors['#rfid-scan'].click();
  const coupon = selectors['#CouponId'];
  for (const [percent, discount, total] of [[10, '$0.30', '$2.70'], [25, '$0.75', '$2.25']]) {
    coupon.selectedOptions = [{dataset: {percent}}];
    coupon.handlers.change();
    assert.equal(selectors['#coupon-breakdown'].hidden, false);
    assert.equal(selectors['#cart-subtotal'].textContent, '$3.00');
    assert.equal(selectors['#cart-discount'].textContent, '−' + discount);
    assert.equal(selectors['#cart-total'].textContent, total);
  }
  coupon.selectedOptions = [];
  coupon.handlers.change();
  assert.equal(selectors['#coupon-breakdown'].hidden, true);
  assert.equal(selectors['#cart-total'].textContent, '$3.00');
});

test('consecutive scans queue while inventory lookup waits and never erase subsequent input', async () => {
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const ui = setup('simulation', [], new Map(), {lookupGate: code => code === '1001' ? gate : Promise.resolve()});
  const input = ui.selectors['#item-code'];
  input.value = '1001';
  const first = input.handlers.keydown({key: 'Enter', preventDefault() {}});
  input.value = '1002';
  const second = input.handlers.keydown({key: 'Enter', preventDefault() {}});
  input.value = 'unfinished';
  assert.equal(ui.selectors['#checkout-button'].disabled, true);
  let prevented = false;
  ui.selectors['#checkout-form'].handlers.submit({preventDefault() { prevented = true; }});
  assert.equal(prevented, true);
  release();
  await Promise.all([first, second]);
  assert.deepEqual(ui.cart(), {'1001': 1, '1002': 1});
  assert.deepEqual(ui.lookups, ['1001', '1002']);
  assert.equal(input.value, 'unfinished');
  assert.equal(ui.selectors['#checkout-button'].disabled, false);
});

test('tag removal names the exact EPC even when another tag was added later', async () => {
  const ui = setup('reader', [{epc: 'AABB', code: '1001'}, {epc: 'CCDD', code: '1001', source: 'manual'}]);
  await new Promise(resolve => setImmediate(resolve));
  const buttons = ui.selectors['#cart-list'].children[0].children.slice(1);
  assert.match(buttons.find(button => button.textContent.includes('AABB')).textContent, /reader-origin event/);
  assert.match(buttons.find(button => button.textContent.includes('CCDD')).textContent, /manual transfer/);
  buttons.find(button => button.textContent.includes('AABB')).click();
  assert.deepEqual(ui.cart(), {'CCDD': 1});
  await ui.poll();
  assert.deepEqual(ui.cart(), {'CCDD': 1});
});

test('manual EPC entry is identified as manual and an unavailable tag is rejected before checkout', async () => {
  const ui = setup('simulation', [], new Map(), {rejectedCodes: ['DEAD']});
  const input = ui.selectors['#item-code'];
  input.value = 'E2801191A503006599BFD10D';
  await input.handlers.keydown({key: 'Enter', preventDefault() {}});
  assert.match(ui.selectors['#cart-list'].children[0].children[1].textContent, /manual entry/);
  input.value = 'DEAD';
  await input.handlers.keydown({key: 'Enter', preventDefault() {}});
  assert.deepEqual(ui.cart(), {'E2801191A503006599BFD10D': 1});
  assert.match(ui.selectors['#scan-error'].textContent, /not available/);
});

test('pairing is an explicit antiforgery POST and never automatic on page open', async () => {
  const ui = setup('reader');
  assert.deepEqual(ui.actions, []);
  await ui.selectors['#pair-reader'].click();
  await ui.selectors['#close-reader'].click();
  assert.deepEqual(ui.actions, ['?handler=PairRfid', '?handler=CloseRfid']);
});

test('repeat form submission is blocked after one logical attempt starts', async () => {
  const ui = setup();
  ui.selectors['#rfid-scan'].click();
  let prevented = false;
  ui.selectors['#checkout-form'].handlers.submit({preventDefault() { prevented = true; }});
  assert.equal(prevented, false);
  assert.equal(ui.selectors['#checkout-button'].disabled, true);
  ui.selectors['#checkout-form'].handlers.submit({preventDefault() { prevented = true; }});
  assert.equal(prevented, true);
});
