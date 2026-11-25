(() => {
    const pollInterval = 1000;
    const staleAfter = 30000;
    const dashboard = document.querySelector("[data-sensor-url]");
    const status = document.querySelector("#sensor-status");
    const motionHistory = document.querySelector("#sensor-motion-history");
    const cards = Array.from(dashboard.querySelectorAll("[data-sensor-card]"), card => ({
        key: card.dataset.sensorCard,
        element: card,
        value: card.querySelector(".sensor-value"),
        meta: card.querySelector(".sensor-card__meta"),
        reading: null
    }));
    const numberFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
    const timeFormat = new Intl.DateTimeFormat(undefined, { timeStyle: "medium" });
    const units = { temperature: "°C", humidity: "% RH", illuminance: "lx" };

    dashboard.querySelector("[data-sensor-cadence]").textContent =
        `Dashboard checks every ${pollInterval / 1000} second`;

    function readingText(key, readings) {
        const value = readings[key];
        if (key === "motion" && typeof value === "boolean") {
            return value ? "Motion detected" : "No motion detected";
        }
        if (Number.isFinite(value)) {
            return `${numberFormat.format(value)} ${units[key]}`;
        }
        if (key === "illuminance" && Number.isFinite(readings.luminousFlux)) {
            return `${numberFormat.format(readings.luminousFlux)} lm`;
        }
        return null;
    }

    function render(readings) {
        const timestamp = Number.isFinite(readings.timestamp) ? readings.timestamp : null;
        const now = Date.now();
        const fresh = timestamp !== null && now - timestamp <= staleAfter;
        for (const card of cards) {
            const text = readingText(card.key, readings);
            if (text !== null) {
                const active = card.key === "motion" && readings.motion === true;
                if (active && fresh && !card.reading?.active) {
                    motionHistory.textContent = `Last motion observed at ${timeFormat.format(timestamp)}`;
                }
                card.reading = { text, timestamp, active };
            }
            if (!card.reading) continue;
            const seenAt = card.reading.timestamp;
            const stale = seenAt !== null && now - seenAt > staleAfter;
            card.element.dataset.state = stale ? "stale" : card.reading.active ? "active" : "normal";
            card.value.textContent = card.reading.text;
            card.meta.textContent = seenAt === null ? "Time unavailable"
                : `${stale ? "Stale · last seen" : "Last seen"} ${timeFormat.format(seenAt)}`;
        }

        const message = readings.status ?? (fresh ? "Receiving sensor data" : "Waiting for a fresh sensor update");
        status.dataset.state = fresh ? "live" : "waiting";
        if (status.textContent !== message) status.textContent = message;
    }

    async function refresh() {
        const started = performance.now();
        try {
            const response = await fetch(dashboard.dataset.sensorUrl, { cache: "no-store" });
            render(await response.json());
        } catch {
            render({ status: "Could not load sensor readings." });
        } finally {
            setTimeout(refresh, Math.max(0, pollInterval - (performance.now() - started)));
        }
    }

    void refresh();
})();
