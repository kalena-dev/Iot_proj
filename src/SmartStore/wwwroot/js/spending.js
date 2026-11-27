(() => {
  const budget = document.querySelector('.spending-budget');
  const input = budget.querySelector('input');
  const message = budget.querySelector('.spending-budget-message');
  const progress = budget.querySelector('progress');
  const spent = Math.round(Number(budget.dataset.spent) * 100);
  const currency = new Intl.NumberFormat('en-CA', { style: 'currency', currency: 'CAD' });

  function render() {
    const target = Math.round(input.valueAsNumber * 100);
    const hasTarget = input.validity.valid && target > 0;
    progress.hidden = message.hidden = !hasTarget;
    const difference = hasTarget ? target - spent : 0;
    budget.classList.toggle('spending-budget--over', hasTarget && difference < 0);
    if (!hasTarget) return;
    progress.value = Math.min(100, spent / target * 100);
    message.textContent = difference >= 0
      ? currency.format(difference / 100) + ' left in budget'
      : currency.format(-difference / 100) + ' over target';
  }

  // Budget targets stay in this page only, including on shared checkout browsers.
  input.value = '';
  input.addEventListener('input', render);
  window.addEventListener('pagehide', () => {
    input.value = '';
    render();
  });
  render();
})();
