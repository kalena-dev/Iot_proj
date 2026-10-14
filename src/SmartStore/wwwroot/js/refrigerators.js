(() => {
  'use strict';
  const error = document.getElementById('iot-error');
  const fanButtons = document.querySelectorAll('#fan-form button');
  const dateText = value => new Date(value).toLocaleString();
  let loading = false;
  let actionBusy = false;

  function render(data) {
    error.hidden = true;
    document.getElementById('mqtt-state').textContent = `MQTT: ${data.brokerConnected ? 'connected' : 'OFFLINE'}`;
    document.getElementById('email-state').textContent = `Email replies: ${data.emailConnected ? 'connected' : 'unavailable / not configured'}`;
    document.getElementById('refresh-time').textContent = `Updated ${dateText(data.timestamp)}`;
    for (const fridge of data.refrigerators) {
      const panel = document.getElementById(fridge.id);
      panel.dataset.state = fridge.status;
      const status = panel.querySelector('[data-status]');
      status.textContent = fridge.status === 'alert' ? 'ABOVE THRESHOLD' : fridge.status === 'offline' ? 'OFFLINE / STALE' : 'NORMAL';
      for (const metric of ['temperature', 'humidity']) {
        const gauge = panel.querySelector(`[data-metric="${metric}"]`);
        const value = fridge[metric];
        gauge.style.setProperty('--fill', `${value === null ? 0 : value / (metric === 'temperature' ? 50 : 100) * 100}%`);
        gauge.querySelector('[data-value]').textContent = value === null ? '—' : Number(value).toFixed(1);
        if (value === null) gauge.removeAttribute('aria-valuenow');
        else gauge.setAttribute('aria-valuenow', value);
        gauge.setAttribute('aria-valuetext', `${value === null ? 'No reading' : value + (metric === 'temperature' ? ' degrees Celsius' : ' percent humidity')}${fridge.stale ? ', stale' : ''}`);
      }
      panel.querySelector('[data-reading]').textContent = fridge.receivedAt
        ? `${fridge.stale ? 'Last known (stale)' : 'Measured'}: ${dateText(fridge.receivedAt)} · threshold ${fridge.threshold} °C`
        : `No readings yet · threshold ${fridge.threshold} °C`;
      const input = panel.querySelector('input[name="threshold"]');
      if (!input.dataset.edited && document.activeElement !== input) input.value = fridge.threshold;
    }
    const fan = data.fan;
    document.getElementById('fan-state').textContent = !fan.online ? 'UNKNOWN · OFFLINE' : `${fan.on ? 'ON' : 'OFF'}${fan.simulated ? ' · SIMULATION' : ''}`;
    document.getElementById('fan-icon').classList.toggle('is-running', fan.online && fan.on);
    document.getElementById('fan-detail').textContent = fan.pending !== null
      ? `Requested ${fan.pending ? 'ON' : 'OFF'} · awaiting Pi confirmation…`
      : fan.online ? 'Raspberry Pi output confirmed. Stops on disconnect or safety timeout.' : 'No fresh hardware confirmation. Check the fan controller.';
    fanButtons.forEach(button => { button.disabled = !fan.online || fan.pending !== null || actionBusy; });
    const list = document.getElementById('iot-notifications');
    list.replaceChildren();
    for (const notification of data.notifications) {
      const li = document.createElement('li');
      li.className = `notification-${notification.kind}`;
      const time = document.createElement('time');
      time.dateTime = notification.createdAt;
      time.textContent = dateText(notification.createdAt);
      const text = document.createElement('span');
      text.textContent = notification.message;
      li.append(time, text);
      list.append(li);
    }
    if (!data.notifications.length) {
      const li = document.createElement('li'); li.textContent = 'No alerts yet.'; list.append(li);
    }
  }
  async function refresh() {
    if (loading) return;
    loading = true;
    try {
      const response = await fetch('/Refrigerators?handler=Status', { cache: 'no-store', signal: AbortSignal.timeout(12000) });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || 'IoT service unavailable');
      render(data);
    } catch (exception) {
      error.textContent = `${exception.message}. Displayed measurements are last known, not live.`;
      error.hidden = false;
      document.getElementById('mqtt-state').textContent = 'IoT service: unavailable';
      document.getElementById('email-state').textContent = 'Email replies: unknown';
      document.querySelectorAll('.fridge-panel').forEach(panel => {
        panel.dataset.state = 'offline'; panel.querySelector('[data-status]').textContent = 'CONNECTION LOST';
      });
      document.getElementById('fan-state').textContent = 'UNKNOWN';
      document.getElementById('fan-icon').classList.remove('is-running');
      fanButtons.forEach(button => { button.disabled = true; });
    } finally { loading = false; }
  }
  document.querySelectorAll('.threshold-form input[name="threshold"]').forEach(input => {
    input.addEventListener('input', () => { input.dataset.edited = 'true'; });
  });
  document.querySelectorAll('.threshold-form, #fan-form').forEach(form => {
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (actionBusy) return;
      actionBusy = true;
      const buttons = form.querySelectorAll('button');
      const body = new FormData(form);
      if (event.submitter?.name) body.set(event.submitter.name, event.submitter.value);
      buttons.forEach(button => { button.disabled = true; });
      const message = document.getElementById('action-message');
      try {
        const response = await fetch(form.action, { method: 'POST', body, signal: AbortSignal.timeout(15000) });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Request rejected');
        message.textContent = data.message || 'Fan command sent. Waiting for hardware confirmation.';
        const input = form.querySelector('input[name="threshold"]');
        if (input) delete input.dataset.edited;
      } catch (exception) { message.textContent = exception.message; }
      finally { actionBusy = false; buttons.forEach(button => { button.disabled = false; }); await refresh(); }
    });
  });
  void refresh();
  setInterval(() => { void refresh(); }, 3000);
})();
