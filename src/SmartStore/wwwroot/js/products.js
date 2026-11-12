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

  // Receive page: picking a product from the list loads its history.
  const productSelect = document.querySelector('select[data-navigate-receive]');
  productSelect?.addEventListener('change', () => {
    const base = productSelect.form.getAttribute('action').split('?')[0].replace(/\/Receive(\/[^/]*)?$/i, '/Receive');
    window.location.href = productSelect.value ? `${base}/${encodeURIComponent(productSelect.value)}` : base;
  });
})();
