document.addEventListener("DOMContentLoaded", () => {
  // Remove budget values saved by older versions; current budgets never persist.
  try {
    for (let index = localStorage.length - 1; index >= 0; index--) {
      const key = localStorage.key(index);
      if (key?.startsWith("smartstore.monthly-budget.")) localStorage.removeItem(key);
    }
  } catch { /* Browser storage may be disabled. */ }
  document.querySelectorAll("[data-dismissible]").forEach((notice) => {
    const closeButton = notice.querySelector(".notice__close");
    closeButton?.addEventListener("click", () => notice.remove());
  });

  const checkoutPanel = document.querySelector("[data-checkout-pages]");
  const checkoutPagination = checkoutPanel?.querySelector("[data-checkout-pagination]");
  if (checkoutPagination) {
    const pages = Array.from(checkoutPanel.querySelectorAll("[data-checkout-page]"));
    const label = checkoutPagination.querySelector("[data-checkout-page-label]");
    const previous = checkoutPagination.querySelector("[data-checkout-prev]");
    const next = checkoutPagination.querySelector("[data-checkout-next]");
    let page = 0;

    function goToPage(index) {
      pages[page].hidden = true;
      page = index;
      pages[page].hidden = false;
      label.textContent = `Page ${page + 1} of ${pages.length}`;
      previous.disabled = page === 0;
      next.disabled = page === pages.length - 1;
    }

    previous.addEventListener("click", () => goToPage(page - 1));
    next.addEventListener("click", () => goToPage(page + 1));
    checkoutPagination.hidden = false;
  }

  const accountProfile = document.querySelector(".account-profile");
  if (accountProfile) {
    document.addEventListener("click", (event) => {
      if (!accountProfile.contains(event.target)) accountProfile.open = false;
    });
    accountProfile.addEventListener("keydown", (event) => {
      if (event.key !== "Escape" || !accountProfile.open) return;
      accountProfile.open = false;
      accountProfile.querySelector("summary").focus();
    });
  }

  const form = document.querySelector("#customer-form");
  const submitButton = form?.querySelector("button[type='submit']");

  form?.addEventListener("submit", (event) => {
    // Let the unobtrusive validation handler run before locking the button.
    window.setTimeout(() => {
      const jqueryValidationPassed =
        typeof window.jQuery === "undefined" ||
        typeof window.jQuery(form).valid !== "function" ||
        window.jQuery(form).valid();

      if (!event.defaultPrevented && jqueryValidationPassed && submitButton) {
        submitButton.disabled = true;
        submitButton.classList.add("is-loading");
        submitButton.setAttribute("aria-busy", "true");
      }
    }, 0);
  });
});
