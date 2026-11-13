
(() => {
  // Existing product data from the ASP.NET checkout page
  const products = new Map(
    [...document.querySelectorAll('.demo-product')].map(el => [
      el.dataset.code,
      {
        name: el.dataset.name,
        price: Number(el.dataset.price),
        stock: Number(el.dataset.stock)
      }
    ])
  );

  const cart = {};

  const codeInput = document.querySelector('#item-code');
  const error = document.querySelector('#scan-error');
  const list = document.querySelector('#cart-list');
  const total = document.querySelector('#cart-total');
  const checkout = document.querySelector('#checkout-button');
  const rfidButton = document.querySelector('#rfid-scan');
  const rfidStatus = document.querySelector('#rfid-status');

  const money = value =>
    new Intl.NumberFormat('en-CA', {
      style: 'currency',
      currency: 'CAD'
    }).format(value);

  // Temporary RFID tags for testing.
  // Each EPC identifies one physical item.
  const demoRfidTags = [
    { epc: 'DEMO-EPC-001', code: '1001' },
    { epc: 'DEMO-EPC-002', code: '1002' },
    { epc: 'DEMO-EPC-003', code: '1001' }
  ];

  // Keep track of which physical RFID tags are in the cart
  const scannedTags = new Map();

  // Display the shopping cart
  function render() {
    list.replaceChildren();

    let amount = 0;

    for (const [code, quantity] of Object.entries(cart)) {
      const product = products.get(code);
      if (!product) continue;

      amount += quantity * product.price;

      const row = document.createElement('div');
      row.className = 'demo-cart-row';

      const name = document.createElement('span');
      name.textContent =
        `${product.name} (${code}) — ${money(product.price)} × ${quantity}`;

      const remove = document.createElement('button');
      remove.type = 'button';
      remove.textContent = 'Remove one';

      remove.addEventListener('click', () => {
        // Remove one item from the cart
        cart[code]--;

        if (cart[code] <= 0) {
          delete cart[code];
        }

        // Keep seen EPCs suppressed after removal until a new checkout session.
        error.textContent = '';
        render();
      });

      row.append(name, remove);
      list.append(row);
    }

    if (!Object.keys(cart).length) {
      list.textContent = 'No items yet.';
    }

    total.textContent = money(amount);

    checkout.disabled =
      Object.keys(cart).length === 0;

    // Preserve existing backend cart format
    document.querySelector('#Cart').value =
      JSON.stringify(cart);
  }

  // Add a product to the cart
  function add(code, focusInput = true) {
    code = code.trim();

    const product = products.get(code);

    if (!product) {
      error.textContent =
        `Unknown item code: ${code || '(empty)'}. Try 1001–1004.`;
      return false;
    }

    if ((cart[code] || 0) >= product.stock) {
      error.textContent =
        `${product.name} is out of stock.`;
      return false;
    }

    cart[code] = (cart[code] || 0) + 1;

    error.textContent = '';
    codeInput.value = '';

    render();

    if (focusInput) {
      codeInput.focus();
    }

    return true;
  }

  // Manual barcode input
  document.querySelector('#add-item').addEventListener(
    'click',
    () => add(codeInput.value)
  );

  codeInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      add(codeInput.value);
    }
  });

  // Existing sample product buttons
  document.querySelectorAll('.demo-product').forEach(el => {
    el.addEventListener('click', () => {
      add(el.dataset.code);
    });
  });

  // Handles multiple RFID tag readings
  function processRfidScan(tags) {
    let added = 0;
    let duplicates = 0;
    let failed = 0;

    const uniqueTags = new Map();

    for (const tag of tags) {
      if (tag.epc && !uniqueTags.has(tag.epc)) {
        uniqueTags.set(tag.epc, tag);
      }
    }

    for (const tag of uniqueTags.values()) {
     
      if (scannedTags.has(tag.epc)) {
        duplicates++;
        continue;
      }

      const product = products.get(tag.code);

      if (!product) {
        failed++;
        continue;
      }

      if ((cart[tag.code] || 0) >= product.stock) {
        failed++;
        continue;
      }

      cart[tag.code] = (cart[tag.code] || 0) + 1;

      scannedTags.set(tag.epc, tag.code);
      added++;
    }

    render();

    if (added > 0) {
      rfidStatus.textContent =
        `${added} RFID item(s) added successfully.`;
    } else if (duplicates > 0 && failed === 0) {
      rfidStatus.textContent =
        'No new RFID items detected. Already scanned.';
    } else {
      rfidStatus.textContent =
        'No items added. Check product codes and stock.';
    }

    if (failed > 0) {
      error.textContent =
        `${failed} RFID item(s) could not be added.`;
    } else {
      error.textContent = '';
    }
  }

 
  rfidButton?.addEventListener('click', () => {
    rfidStatus.textContent = 'Scanning RFID tags...';

    processRfidScan(demoRfidTags);
  });

  const scanner = document.querySelector('.rfid-scanner');
  async function pollReader() {
    try {
      const url = new URL(window.location.href);
      url.search = new URLSearchParams({ handler: 'Rfid' });
      const response = await fetch(url, { cache: 'no-store', headers: { 'X-Rfid-Session': scanner.dataset.session } });
      if (response.status === 404) {
        rfidStatus.textContent = 'RFID session expired. Reload and pair the Pi again.';
        return;
      }
      if (!response.ok) throw new Error('Reader bridge unavailable');
      const tags = await response.json();
      if (tags.length) processRfidScan(tags);
      else rfidStatus.textContent = 'Waiting for RFID events. No tags received.';
    } catch {
      rfidStatus.textContent = 'RFID bridge unavailable. Manual checkout is still available.';
    }
    window.setTimeout(pollReader, 1500);
  }
  if (scanner.dataset.mode === 'reader' && scanner.dataset.session) pollReader();

  render();
})();
