
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
  let scanQueue = Promise.resolve();
  let pendingScans = 0;
  let submitting = false;
  let unmatchedCode = null;
  const matchLabel = document.querySelector('#barcode-match');
  const matchSelect = document.querySelector('#barcode-product');
  const error = document.querySelector('#scan-error');
  const list = document.querySelector('#cart-list');
  const total = document.querySelector('#cart-total');
  const couponButton = document.querySelector('#add-coupon');
  const couponPanel = document.querySelector('#checkout-coupons');
  const couponSelect = document.querySelector('#CouponId');
  const couponBreakdown = document.querySelector('#coupon-breakdown');
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

  // An entered EPC identifies one unit; neither an identifier nor a bridge event authenticates a tag.
  // Keep removed EPCs in the seen set so a repeating reader event cannot add them back.
  const scannedTags = new Map();

  // The tags of each cart line, so checkout sells those exact units. The rest of a line was added by barcode.
  // Simulated demo tags are not registered in inventory, so they count as barcode units.
  const cartTags = new Map();
  const tagSources = new Map();
  function rememberTag(code, epc, source) {
    if (epc.startsWith('DEMO-')) return;
    if (!cartTags.has(code)) cartTags.set(code, []);
    cartTags.get(code).push(epc);
    tagSources.set(epc, source);
  }

  // Display the shopping cart
  function render() {
    list.replaceChildren();

    let amount = 0;

    for (const [code, quantity] of Object.entries(cart)) {
      const product = products.get(code);
      if (!product) continue;

      amount += quantity * Math.round(product.price * 100);

      const row = document.createElement('div');
      row.className = 'demo-cart-row';

      const name = document.createElement('span');
      name.textContent =
        `${product.name} (${code}) — ${money(product.price)} × ${quantity}`;

      row.append(name);
      const tags = cartTags.get(code) || [];
      if (quantity > tags.length) {
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.textContent = 'Remove barcode unit';
        remove.addEventListener('click', () => {
          if (--cart[code] === 0) delete cart[code];
          error.textContent = '';
          render();
        });
        row.append(remove);
      }
      for (const epc of [...tags].reverse()) {
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.textContent = `Remove ${epc} · ${tagSources.get(epc)}`;
        remove.addEventListener('click', () => {
          tags.splice(tags.indexOf(epc), 1);
          if (!tags.length) cartTags.delete(code);
          if (--cart[code] === 0) delete cart[code];
          error.textContent = '';
          render();
        });
        row.append(remove);
      }
      list.append(row);
    }

    if (!Object.keys(cart).length) {
      list.textContent = 'No items yet.';
    }

    const percent = Number(couponSelect?.selectedOptions[0]?.dataset.percent || 0);
    const discount = Math.round(amount * percent / 100);
    if (couponBreakdown) {
      couponBreakdown.hidden = percent === 0;
      document.querySelector('#coupon-label').textContent = `Loyalty coupon (${percent}%)`;
      document.querySelector('#cart-subtotal').textContent = money(amount / 100);
      document.querySelector('#cart-discount').textContent = `−${money(discount / 100)}`;
    }
    total.textContent = money((amount - discount) / 100);

    checkout.disabled =
      Object.keys(cart).length === 0 || pendingScans > 0 || submitting;

    // Barcode units as { code: quantity }, each scanned tag as { epc: 1 } so that exact unit is sold
    const payload = {};
    for (const [code, quantity] of Object.entries(cart)) {
      const tags = cartTags.get(code) || [];
      if (quantity > tags.length) payload[code] = quantity - tags.length;
      for (const epc of tags) payload[epc] = 1;
    }
    document.querySelector('#Cart').value =
      JSON.stringify(payload);
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

    render();

    if (focusInput) {
      codeInput.focus();
    }

    return true;
  }

  function resetMatch() {
    if (!matchLabel) return;
    matchLabel.hidden = true;
    matchSelect.value = '';
    unmatchedCode = null;
  }

  async function resolveManual(barcode, assignedUpc) {
    try {
      const response = assignedUpc
        ? await fetch('?handler=AssignCode', { method: 'POST', body: new URLSearchParams({
            code: barcode, upc: assignedUpc,
            __RequestVerificationToken: document.querySelector('[name="__RequestVerificationToken"]').value
          }) })
        : await fetch('?handler=Code&code=' + encodeURIComponent(barcode), { cache: 'no-store' });
      if (!assignedUpc && response.status === 404) {
        if (!matchLabel) throw new Error('This code is not in inventory. Choose an available product or ask store staff.');
        unmatchedCode = barcode;
        matchLabel.hidden = false;
        if (!codeInput.value) codeInput.value = barcode;
        error.textContent = `Choose a product for ${barcode}, then press Enter to save it.`;
        matchSelect.focus();
        return;
      }
      if (response.status === 409)
        throw new Error(assignedUpc ? 'This code already belongs to another product.' : 'This tag is not available for checkout.');
      if (!response.ok) throw new Error('Could not verify this code with inventory.');
      const {code, isTag} = await response.json();
      const epc = barcode.toUpperCase();
      if (isTag && scannedTags.has(epc)) {
        error.textContent = 'This tag has already been scanned.';
        return;
      }
      if (add(code, false)) {
        if (isTag) {
          scannedTags.set(epc, code);
          rememberTag(code, epc, 'manual entry');
          render();
        }
        resetMatch();
      }
    } catch (failure) {
      error.textContent = failure.message;
    }
  }

  function addManual() {
    const barcode = codeInput.value.trim();
    if (!barcode) {
      error.textContent = 'Scan or enter a code.';
      return Promise.resolve();
    }
    const assignedUpc = unmatchedCode === barcode && matchLabel && !matchLabel.hidden ? matchSelect.value : '';
    // Capture every complete scan immediately. Inventory responses must never clear or discard the next scan.
    codeInput.value = '';
    pendingScans++;
    render();
    const operation = scanQueue.then(() => resolveManual(barcode, assignedUpc)).finally(() => {
      pendingScans--;
      render();
    });
    scanQueue = operation;
    return operation;
  }

  codeInput.addEventListener('input', () => {
    resetMatch();
    error.textContent = '';
  });
  couponButton.addEventListener('click', () => {
    couponPanel.hidden = !couponPanel.hidden;
    couponButton.setAttribute('aria-expanded', String(!couponPanel.hidden));
    if (!couponPanel.hidden) couponSelect?.focus();
  });
  couponSelect?.addEventListener('change', render);

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

    // Keyed by EPC, not by reader event (scanId): showing the same tag again is not another unit.
    for (const tag of tags) {
      const epc = tag.epc?.startsWith('DEMO-') ? tag.epc : tag.epc?.trim().toUpperCase();
      if (epc && !uniqueTags.has(epc)) {
        uniqueTags.set(epc, {...tag, epc});
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
      rememberTag(tag.code, tag.epc, tag.source === 'manual' ? 'manual transfer' : 'reader-origin event');
      added++;
    }

    render();

    if (added > 0) {
      rfidStatus.textContent =
        `${added} item(s) added (${tags.some(tag => tag.epc?.startsWith('DEMO-')) ? 'simulation' : tags.some(tag => tag.source === 'manual') ? 'manual transfer / reader-origin events' : 'reader-origin events'}).`;
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
  if (scanner.dataset.session) {
    const url = new URL(window.location.href);
    url.searchParams.set('rfidSession', scanner.dataset.session);
    const attempt = document.querySelector('#AttemptId');
    if (attempt?.value) url.searchParams.set('attemptId', attempt.value);
    window.history?.replaceState(null, '', url);
    try { window.sessionStorage?.setItem('northstar.checkout-rfid', scanner.dataset.session); } catch { /* Browser storage can be disabled. */ }
  }
  async function readerAction(handler) {
    try {
      const response = await fetch(`?handler=${handler}`, {
        method: 'POST', headers: { 'X-Rfid-Session': scanner.dataset.session },
        body: new URLSearchParams({ __RequestVerificationToken: document.querySelector('[name="__RequestVerificationToken"]').value })
      });
      if (response.status === 409) throw new Error('The reader is paired elsewhere. Release that session first.');
      if (!response.ok) throw new Error('Reader pairing could not be changed.');
      rfidStatus.textContent = handler === 'PairRfid' ? 'Reader paired. Waiting for events.' : 'Reader released.';
    } catch (failure) { rfidStatus.textContent = failure.message; }
  }
  document.querySelector('#pair-reader')?.addEventListener('click', () => readerAction('PairRfid'));
  document.querySelector('#close-reader')?.addEventListener('click', () => readerAction('CloseRfid'));
  document.querySelector('#checkout-form')?.addEventListener('submit', event => {
    if (pendingScans > 0 || submitting) {
      event.preventDefault();
      error.textContent = pendingScans > 0 ? 'Wait for the queued codes to finish.' : 'Checkout is already being submitted.';
      return;
    }
    submitting = true;
    codeInput.disabled = true;
    render();
  });
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
      const newTags = tags.filter(tag => !scannedTags.has(tag.epc));
      if (newTags.length) processRfidScan(newTags);
      else if (!tags.length) rfidStatus.textContent = 'No reader events received. Pair the reader or enter codes manually.';
    } catch {
      rfidStatus.textContent = 'RFID bridge unavailable. Manual checkout is still available.';
    }
    window.setTimeout(pollReader, 350);
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
})();
