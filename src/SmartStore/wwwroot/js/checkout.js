
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
  let scanPending = false;
  const matchLabel = document.querySelector('#barcode-match');
  const matchSelect = document.querySelector('#barcode-product');
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

  // Distinct virtual item tags use the current catalog, not a second hardcoded one.
  const demoRfidTags = [...products].filter(([, product]) => product.stock > 0)
    .slice(0, 2).flatMap(([code, product], index) =>
      Array.from({ length: Math.min(index === 0 ? 2 : 1, product.stock) },
        (_, unit) => ({ epc: `DEMO-${code}-${unit}`, code })));

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
        'Choose an available product.';
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

  function resetMatch() {
    matchLabel.hidden = true;
    matchSelect.value = '';
  }

  async function addManual() {
    const barcode = codeInput.value.trim();
    if (scanPending) return;
    if (!barcode) {
      error.textContent = 'Scan or enter a code.';
      return;
    }
    scanPending = true;
    try {
      const assigning = !matchLabel.hidden && Boolean(matchSelect.value);
      const response = assigning
        ? await fetch('?handler=AssignCode', { method: 'POST', body: new URLSearchParams({
            code: barcode, upc: matchSelect.value,
            __RequestVerificationToken: document.querySelector('[name="__RequestVerificationToken"]').value
          }) })
        : await fetch('?handler=Code&code=' + encodeURIComponent(barcode), { cache: 'no-store' });
      if (codeInput.value.trim() !== barcode) return;
      if (!assigning && response.status === 404) {
        matchLabel.hidden = false;
        error.textContent = 'Choose a product for this code, then select Add item to save it.';
        matchSelect.focus();
        return;
      }
      if (response.status === 409) throw new Error('This code already belongs to another product. Scan it again.');
      if (!response.ok) throw new Error('Could not verify this code with inventory.');
      const {code, isTag} = await response.json();
      const epc = barcode.toUpperCase();
      if (isTag && scannedTags.has(epc)) {
        error.textContent = 'This tag has already been scanned.';
        return;
      }
      if (add(code)) {
        if (isTag) scannedTags.set(epc, code);
        resetMatch();
      }
    } catch (failure) {
      error.textContent = failure.message;
    } finally {
      scanPending = false;
    }
  }

  codeInput.addEventListener('input', () => {
    resetMatch();
    error.textContent = '';
  });
  document.querySelector('#add-item').addEventListener('click', addManual);

  codeInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      return addManual();
    }
  });

  // Existing sample product buttons
  document.querySelectorAll('.demo-product').forEach(el => {
    el.addEventListener('click', () => {
      if (add(el.dataset.code)) resetMatch();
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
// Inline transaction details and sample catalog pagination.
(() => {
  const sampleCards = [...document.querySelectorAll('#sample-grid .checkout-sample')];
  const pageSize = 4;
  let page = 0;
  const totalPages = Math.max(1, Math.ceil(sampleCards.length / pageSize));
  const prev = document.getElementById('samples-prev');
  const next = document.getElementById('samples-next');
  function renderSamples() {
    sampleCards.forEach((card, index) => { card.hidden = index < page * pageSize || index >= (page + 1) * pageSize; });
    document.getElementById('sample-page-label').textContent = `Page ${page + 1} of ${totalPages}`;
    prev.disabled = page === 0;
    next.disabled = page === totalPages - 1;
  }
  prev.addEventListener('click', () => { page = Math.max(0, page - 1); renderSamples(); });
  next.addEventListener('click', () => { page = Math.min(totalPages - 1, page + 1); renderSamples(); });
  renderSamples();

  const detail = document.getElementById('checkout-detail');
  const title = document.getElementById('checkout-detail-title');
  const body = document.getElementById('checkout-detail-body');
  const receiptButton = document.getElementById('show-receipt');
  const money = amount => new Intl.NumberFormat('en-CA', {style:'currency', currency:'CAD'}).format(amount);
  const showError = message => { body.textContent = message; };
  const showPanel = name => { title.textContent = name; detail.hidden = false; body.replaceChildren(); };
  document.getElementById('close-checkout-detail').addEventListener('click', () => { detail.hidden = true; });

  async function displayReceipt(id) {
    if (!id) return;
    showPanel('Purchase receipt');
    body.textContent = 'Loading receipt…';
    try {
      const response = await fetch(`?handler=Receipt&id=${encodeURIComponent(id)}`);
      if (!response.ok) throw new Error('Receipt could not be loaded.');
      const result = await response.json();
      body.replaceChildren();
      const receipt = document.createElement('pre');
      receipt.className = 'checkout-receipt-text';
      receipt.textContent = result.receipt;
      const print = document.createElement('button');
      print.type = 'button'; print.className = 'checkout-small-button'; print.textContent = 'Print receipt';
      print.addEventListener('click', () => {
        const printWindow = window.open('', '_blank');
        if (!printWindow) { showError('Allow pop-ups to print the receipt.'); return; }
        const pre = printWindow.document.createElement('pre');
        pre.textContent = result.receipt;
        printWindow.document.body.append(pre);
        printWindow.document.close(); printWindow.focus(); printWindow.print();
      });
      body.append(receipt, print);
    } catch (error) { showError(error.message); }
  }
  receiptButton.addEventListener('click', () => displayReceipt(receiptButton.dataset.receiptId));
  if (receiptButton.dataset.receiptId) displayReceipt(receiptButton.dataset.receiptId);

  document.getElementById('show-history').addEventListener('click', () => {
    showPanel('Purchase history');
    const form = document.createElement('form');
    form.className = 'checkout-history-form';
    const input = document.createElement('input');
    input.type = 'email'; input.required = true; input.maxLength = 254;
    input.placeholder = 'Email used at checkout';
    input.value = document.getElementById('CustomerEmail')?.value || '';
    const search = document.createElement('button');
    search.type = 'submit'; search.className = 'checkout-small-button'; search.textContent = 'Search';
    const results = document.createElement('div');
    results.className = 'checkout-history-results';
    form.append(input, search); body.append(form, results);
    form.addEventListener('submit', async event => {
      event.preventDefault(); results.textContent = 'Loading purchases…';
      try {
        const response = await fetch(`?handler=History&email=${encodeURIComponent(input.value.trim())}`);
        if (!response.ok) throw new Error('Could not load history. Check the email and database connection.');
        const sales = await response.json();
        results.replaceChildren();
        if (!sales.length) { results.textContent = 'No purchases found for this email.'; return; }
        sales.forEach(sale => {
          const row = document.createElement('div'); row.className = 'checkout-history-row';
          const summary = document.createElement('div');
          const date = new Date(sale.date).toLocaleDateString('en-CA');
          const heading = document.createElement('strong'); heading.textContent = `${money(sale.total)} · ${sale.status}`;
          const sub = document.createElement('small'); sub.textContent = `${date} · ${sale.points} points`;
          summary.append(heading, sub); row.append(summary);
          if (sale.status === 'approved') {
            const view = document.createElement('button');
            view.type = 'button'; view.className = 'checkout-small-button'; view.textContent = 'Receipt';
            view.addEventListener('click', () => displayReceipt(sale.id)); row.append(view);
          }
          results.append(row);
        });
      } catch (error) { results.textContent = error.message; }
    });
  });
})();
