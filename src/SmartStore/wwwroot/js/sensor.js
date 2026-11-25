(() => {
    const dashboard = document.querySelector("[data-sensor-url]");
    const status = document.querySelector("#sensor-status");
    const widgets = Array.from(dashboard.querySelectorAll("[data-reading]"));
    const numberFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
    const units = { temperature: "°C", humidity: "% RH", illuminance: "lx" };
    const latest = new Map();

    function render(readings) {
        const timestamp = readings.timestamp ?? null;
        const now = Date.now();
        for (const widget of widgets) {
            const key = widget.dataset.reading;
            const value = readings[key];
            let text;
            if (key === "motion" && typeof value === "boolean") {
                text = value ? "Motion detected" : "No motion detected";
            } else if (key in units && Number.isFinite(value)) {
                text = `${numberFormat.format(value)} ${units[key]}`;
            } else if (key === "illuminance" && Number.isFinite(readings.luminousFlux)) {
                text = `${numberFormat.format(readings.luminousFlux)} lm`;
            }

            if (text) latest.set(key, { text, timestamp });
            const reading = latest.get(key);
            if (reading) {
                const stale = reading.timestamp !== null && now - reading.timestamp > 30000;
                widget.textContent = stale ? `${reading.text} (stale)` : reading.text;
            } else {
                widget.textContent = "No reading";
            }
        }

        const update = new Date(timestamp ?? NaN);
        const message = readings.status ?? (Number.isNaN(update.getTime())
            ? "Waiting for a timestamped sensor update."
            : `Last sensor update · ${update.toLocaleString()}`);

        if (status.textContent !== message) status.textContent = message;
    }

    async function refresh() {
        try {
            const response = await fetch(dashboard.dataset.sensorUrl, { cache: "no-store" });
            render(await response.json());
        } catch {
            render({ status: "Could not load sensor readings." });
        } finally {
            setTimeout(refresh, 2000);
        }
    }

    void refresh();
})();
