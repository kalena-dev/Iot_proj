// Scanner support for the product pages.
// USB barcode scanners and the RFID reader (in keyboard mode) "type" the code and
// then press Enter. On a form, that Enter would submit before the other fields
// are filled, so in code fields it moves to the next field instead.
(() => {
  document.querySelectorAll('input[data-scan]').forEach(input => {
    input.addEventListener('keydown', event => {
      if (event.key !== 'Enter') return;
      event.preventDefault();
      input.value = input.value.trim();
      const fields = [...input.form.querySelectorAll('input:not([type=hidden]), select, textarea')];
      const next = fields[fields.indexOf(input) + 1];
      if (next) next.focus();
    });
  });

  // Search / lookup boxes: Enter submits (normal behaviour) after trimming the scan.
  document.querySelectorAll('input[data-scan-submit]').forEach(input => {
    input.form?.addEventListener('submit', () => { input.value = input.value.trim(); });
  });

  // Receive page: count the scanned tags, and generate test tags when no reader is at hand.
  const tagBox = document.querySelector('textarea[data-receive-tags]');
  const tagCount = document.querySelector('[data-tag-count]');
  const readEnteredTags = () => tagBox.value.split(/[\s,;]+/).filter(Boolean).map(tag => tag.toUpperCase());
  const countTags = () => {
    const tags = new Set(readEnteredTags());
    tagCount.textContent = `${tags.size} tag${tags.size === 1 ? '' : 's'}`;
  };
  if (tagBox && tagCount) {
    tagBox.addEventListener('input', countTags);
    countTags();
    document.querySelector('[data-simulate-tags]')?.addEventListener('click', () => {
      // 96-bit EPCs (24 hex digits), random so each one is a new unit.
      const bytes = () => [...crypto.getRandomValues(new Uint8Array(12))]
        .map(b => b.toString(16).padStart(2, '0')).join('').toUpperCase();
      const fresh = Array.from({ length: 3 }, bytes);
      tagBox.value = [tagBox.value.trim(), ...fresh].filter(Boolean).join('\n') + '\n';
      countTags();
      tagBox.focus();
    });
  }

  const reader = document.querySelector('[data-receive-reader]');
  if (reader && tagBox && tagCount) {
    const seen = new Set(readEnteredTags());
    const url = new URL(window.location.href);
    url.search = new URLSearchParams({ handler: 'Rfid' });
    const poll = async () => {
      try {
        const response = await fetch(url, {
          cache: 'no-store',
          headers: { 'X-Rfid-Session': reader.dataset.receiveReader }
        });
        if (response.status === 404 || response.redirected) {
          reader.textContent = 'Reader session ended. Reload this page to reconnect.';
          return;
        }
        if (!response.ok) throw new Error('Reader unavailable');
        const entered = new Set(readEnteredTags());
        const fresh = [];
        for (const epc of await response.json()) {
          if (!seen.has(epc) && !entered.has(epc)) fresh.push(epc);
          seen.add(epc);
        }
        if (fresh.length) {
          tagBox.value = [tagBox.value.trimEnd(), ...fresh].filter(Boolean).join('\n') + '\n';
          countTags();
        }
        reader.textContent = seen.size ? 'Scan more tags or save this delivery.' : 'Waiting for an RFID tag.';
      } catch {
        reader.textContent = 'Reader unavailable. You can still enter EPCs manually.';
      }
      window.setTimeout(poll, 350);
    };
    poll();
  }

  // Receive page: picking a product from the list loads its history.
  const productSelect = document.querySelector('select[data-navigate-receive]');
  productSelect?.addEventListener('change', () => {
    const base = window.location.pathname.replace(/\/Receive(\/[^/]*)?$/i, '/Receive');
    window.location.href = productSelect.value ? `${base}/${encodeURIComponent(productSelect.value)}` : base;
  });
})();
