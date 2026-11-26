(() => {
  const inventory = document.querySelector('[data-inventory-url]');
  const rows = inventory.querySelector('[data-inventory-rows]');
  const status = inventory.querySelector('[data-inventory-status]');

  const search = inventory.querySelector('[data-inventory-search]');
  const filter = inventory.querySelector('[data-inventory-filter]');
  const empty = inventory.querySelector('[data-inventory-empty]');

  function applyFilters() {
    const term = search.value.trim().toLowerCase();
    let matches = 0;
    for (const row of rows.rows) {
      row.hidden = ![row.cells[0], row.cells[1]].some(cell => cell.textContent.toLowerCase().includes(term))
        || (filter.value !== '' && row.dataset.stockClass !== filter.value);
      if (!row.hidden) matches++;
    }
    empty.hidden = matches > 0;
  }

  search.addEventListener('input', applyFilters);
  filter.addEventListener('change', applyFilters);

  async function refresh() {
    try {
      if (document.hidden) return;
      const response = await fetch(inventory.dataset.inventoryUrl, { cache: 'no-store' });
      if (!response.ok || response.redirected) throw new Error('Inventory unavailable');
      const products = await response.json();
      const fragment = document.createDocumentFragment();
      for (const product of products) {
        const row = document.createElement('tr');
        row.dataset.stockClass = product.stockClass;
        const name = document.createElement('th');
        name.scope = 'row';
        name.textContent = product.name;
        row.append(name);
        const code = row.insertCell();
        code.textContent = product.code;
        const units = row.insertCell();
        units.className = 'num';
        units.textContent = product.stock;
        const badge = document.createElement('span');
        badge.className = 'stock ' + product.stockClass;
        badge.textContent = product.status;
        row.insertCell().append(badge);
        fragment.append(row);
      }
      rows.replaceChildren(fragment);
      applyFilters();
      status.textContent = '';
    } catch {
      status.textContent = 'Unable to refresh stock. Retrying…';
    } finally {
      window.setTimeout(refresh, 5000);
    }
  }

  window.setTimeout(refresh, 5000);
})();
