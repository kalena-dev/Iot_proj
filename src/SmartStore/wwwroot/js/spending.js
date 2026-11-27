(() => {
  const budget = document.querySelector('.spending-budget');
  const input = budget.querySelector('input');
  const message = budget.querySelector('.spending-budget-message');
  const progress = budget.querySelector('progress');
  const key = 'smartstore.monthly-budget.' + budget.dataset.budgetKey;
  const spent = Math.round(Number(budget.dataset.spent) * 100);
  const currency = new Intl.NumberFormat('en-CA', { style: 'currency', currency: 'CAD' });

  function render() {
    const target = Math.round(input.valueAsNumber * 100);
    const hasTarget = input.validity.valid && target > 0;
    progress.hidden = message.hidden = !hasTarget;
    if (!hasTarget) return;
    progress.value = Math.min(100, spent / target * 100);
    const difference = target - spent;
    message.textContent = difference >= 0
      ? currency.format(difference / 100) + ' left in budget'
      : currency.format(-difference / 100) + ' over target';
    budget.classList.toggle('spending-budget--over', difference < 0);
  }

  try { input.value = localStorage.getItem(key) || ''; } catch {}
  input.addEventListener('input', render);
  input.addEventListener('change', () => {
    if (!input.validity.valid) return;
    try {
      if (input.valueAsNumber > 0) localStorage.setItem(key, input.value);
      else localStorage.removeItem(key);
    } catch {}
  });
  render();
})();
