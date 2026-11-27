(() => {
  const key = "northstar-theme";
  const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");
  const root = document.documentElement;
  const themeColor = document.querySelector('meta[name="theme-color"]');
  let button;

  function readTheme() {
    try {
      const value = localStorage.getItem(key);
      return value === "light" || value === "dark" ? value : null;
    } catch {
      return null;
    }
  }

  let preference = readTheme();

  function applyTheme() {
    const dark = (preference ?? (systemTheme.matches ? "dark" : "light")) === "dark";
    root.dataset.theme = dark ? "dark" : "light";
    themeColor.content = dark ? "#101923" : "#f3f5f6";
    button?.setAttribute("aria-pressed", String(dark));
  }

  applyTheme();

  document.addEventListener("DOMContentLoaded", () => {
    button = document.querySelector(".theme-toggle");
    button.addEventListener("click", () => {
      preference = root.dataset.theme === "dark" ? "light" : "dark";
      try {
        localStorage.setItem(key, preference);
      } catch {
        // The toggle still works on this page when storage is blocked.
      }
      applyTheme();
    });
    applyTheme();
    button.hidden = false;
  });

  systemTheme.addEventListener("change", applyTheme);
  window.addEventListener("storage", event => {
    if (event.key === key || event.key === null) {
      preference = readTheme();
      applyTheme();
    }
  });
})();
