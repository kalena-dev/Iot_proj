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
  let closeReader = async () => true;
  if (reader && tagBox && tagCount) {
    const token = reader.dataset.receiveReader;
    const storageKey = `northstar:receive-reader:${window.location.pathname}`;
    const pageUrl = new URL(window.location.href);
    let remembered;
    try { remembered = sessionStorage.getItem(storageKey); } catch { /* URL reuse still works. */ }
    if (!pageUrl.searchParams.has('rfidSession') && remembered && remembered !== token) {
      pageUrl.searchParams.set('rfidSession', remembered);
      window.location.replace(pageUrl);
      return;
    }
    // Keep the server-issued token in this tab's URL so a reload reuses its exact owned session.
    // Never recover a token from another tab or silently pair on page load.
    pageUrl.searchParams.set('rfidSession', token);
    history.replaceState(null, '', pageUrl);
    try { sessionStorage.setItem(storageKey, token); } catch { /* URL reuse still works. */ }
    const seen = new Set(readEnteredTags());
    let ended = false;
    const handlerUrl = handler => {
      const url = new URL(window.location.href);
      url.search = new URLSearchParams({ handler });
      return url;
    };
    const post = async handler => {
      const verification = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
      if (!verification) return false;
      const body = new URLSearchParams({ __RequestVerificationToken: verification });
      const response = await fetch(handlerUrl(handler), {
        method: 'POST', cache: 'no-store', body,
        headers: { 'X-Rfid-Session': token }
      });
      return response.ok && !response.redirected;
    };
    document.querySelector('[data-pair-reader]')?.addEventListener('click', async event => {
      event.preventDefault();
      try {
        reader.textContent = await post('PairRfid')
          ? 'Reader paired. Manual entry is also available.'
          : 'Reader is unavailable or paired elsewhere. Release its other session first.';
      } catch {
        reader.textContent = 'Pairing unavailable. You can still enter EPCs manually.';
      }
    });
    closeReader = async () => {
      if (ended) return true;
      if (!await post('CloseRfid')) return false;
      ended = true;
      try { sessionStorage.removeItem(storageKey); } catch { /* No persistence available. */ }
      const url = new URL(window.location.href);
      url.searchParams.delete('rfidSession');
      history.replaceState(null, '', url);
      reader.textContent = 'Reader released. Reload to start a new session, or enter EPCs manually.';
      return true;
    };
    document.querySelector('[data-close-reader]')?.addEventListener('click', async event => {
      event.preventDefault();
      try {
        if (!await closeReader()) reader.textContent = 'Could not release this reader session.';
      } catch { reader.textContent = 'Could not release this reader session.'; }
    });
    const poll = async () => {
      if (ended) return;
      try {
        const response = await fetch(handlerUrl('Rfid'), {
          cache: 'no-store', headers: { 'X-Rfid-Session': token }
        });
        if (response.status === 404 || response.redirected) {
          ended = true;
          reader.textContent = 'Reader session ended. Reload to start a new session.';
          return;
        }
        if (!response.ok) throw new Error('Reader unavailable');
        const entered = new Set(readEnteredTags());
        const fresh = [];
        const origins = new Set();
        for (const tag of await response.json()) {
          const epc = tag.epc;
          if (!seen.has(epc) && !entered.has(epc)) {
            fresh.push(epc);
            origins.add(tag.source);
          }
          seen.add(epc);
        }
        if (fresh.length && !ended) {
          tagBox.value = [tagBox.value.trimEnd(), ...fresh].filter(Boolean).join('\n') + '\n';
          countTags();
          reader.textContent = origins.has('manual')
            ? 'Manually transferred tags added. You can also enter EPCs here.'
            : 'Reader-origin tags added. Manual entry is also available.';
        }
      } catch {
        reader.textContent = 'Reader unavailable. You can still enter EPCs manually.';
      }
      if (!ended) window.setTimeout(poll, 350);
    };
    poll();
  }

  // Receive page: picking a product from the list loads its history.
  const productSelect = document.querySelector('select[data-navigate-receive]');
  productSelect?.addEventListener('change', async () => {
    // A different delivery must not inherit tags retained by this receiving session.
    try {
      if (!await closeReader()) {
        if (reader) reader.textContent = 'Release the reader before changing products.';
        return;
      }
    } catch {
      if (reader) reader.textContent = 'Release the reader before changing products.';
      return;
    }
    const base = window.location.pathname.replace(/\/Receive(\/[^/]*)?$/i, '/Receive');
    window.location.href = productSelect.value ? `${base}/${encodeURIComponent(productSelect.value)}` : base;
  });
})();
