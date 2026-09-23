document.addEventListener("DOMContentLoaded", () => {
  document.querySelectorAll("[data-dismissible]").forEach((notice) => {
    const closeButton = notice.querySelector(".notice__close");
    closeButton?.addEventListener("click", () => notice.remove());
  });

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
