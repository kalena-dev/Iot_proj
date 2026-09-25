(() => {
  const products = new Map([...document.querySelectorAll('.demo-product')].map(el => [el.dataset.code, {
    name: el.dataset.name, price: Number(el.dataset.price), stock: Number(el.dataset.stock)
  }]));
  const cart = {};
  const codeInput = document.querySelector('#item-code');
  const error = document.querySelector('#scan-error');
  const list = document.querySelector('#cart-list');
  const total = document.querySelector('#cart-total');
  const checkout = document.querySelector('#checkout-button');
  const money = value => new Intl.NumberFormat('en-CA', { style: 'currency', currency: 'CAD' }).format(value);

  function render() {
    list.replaceChildren();
    let amount = 0;
    for (const [code, quantity] of Object.entries(cart)) {
      const product = products.get(code);
      amount += quantity * product.price;
      const row = document.createElement('div');
      row.className = 'demo-cart-row';
      const name = document.createElement('span');
      name.textContent = `${product.name} (${code}) — ${money(product.price)} × ${quantity}`;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.textContent = 'Remove one';
      remove.addEventListener('click', () => {
        if (--cart[code] === 0) delete cart[code];
        render();
      });
      row.append(name, remove);
      list.append(row);
    }
    if (!Object.keys(cart).length) list.textContent = 'No items yet.';
    total.textContent = money(amount);
    checkout.disabled = Object.keys(cart).length === 0;
    document.querySelector('#Cart').value = JSON.stringify(cart);
  }

  function add(code) {
    code = code.trim();
    const product = products.get(code);
    if (!product) error.textContent = `Unknown item code: ${code || '(empty)'}. Try 1001–1004.`;
    else if ((cart[code] || 0) >= product.stock) error.textContent = `${product.name} is out of stock.`;
    else { cart[code] = (cart[code] || 0) + 1; error.textContent = ''; render(); }
    codeInput.value = '';
    codeInput.focus();
  }
  document.querySelector('#add-item').addEventListener('click', () => add(codeInput.value));
  codeInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); add(codeInput.value); }
  });
  document.querySelectorAll('.demo-product').forEach(el => el.addEventListener('click', () => add(el.dataset.code)));
  render();
})();
