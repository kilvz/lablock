Router.register('dashboard', async (container) => {
  try {
    const res = await api('/api/clients');
    const data = await res.json();

    const online = data.clients.filter(c => c.isOnline).length;
    const offline = data.total - online;

    container.innerHTML = `
      <div class="page-header">
        <h1 class="page-title">Dashboard</h1>
        <span class="text-muted">${data.clients.length} clients</span>
      </div>
      <div class="stats-grid">
        <div class="stat-card">
          <div class="stat-label">Total Clients</div>
          <div class="stat-value">${data.total}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Online</div>
          <div class="stat-value online">${online}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Offline</div>
          <div class="stat-value offline">${offline}</div>
        </div>
        <div class="stat-card">
          <div class="stat-label">Server Uptime</div>
          <div class="stat-value" id="uptime">-</div>
        </div>
      </div>
      <div class="card">
        <div class="card-header">
          <h2 class="card-title">Online Clients</h2>
        </div>
        <div class="table-container">
          <table>
            <thead>
              <tr>
                <th>Client ID</th>
                <th>Hostname</th>
                <th>User</th>
                <th>Active App</th>
                <th>CPU</th>
                <th>Memory</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              ${data.clients.filter(c => c.isOnline).map(c => `
                <tr>
                  <td><a href="#clients?client=${c.clientId}">${c.clientId}</a></td>
                  <td>${c.hostname || '-'}</td>
                  <td>${c.interactiveUser || c.currentUser || '-'}</td>
                  <td>${c.activeProcess || '-'}</td>
                  <td>${c.cpuPercent != null ? c.cpuPercent.toFixed(1) + '%' : '-'}</td>
                  <td>${c.memoryPercent != null ? c.memoryPercent.toFixed(1) + '%' : '-'}</td>
                  <td><span class="status-badge online">Online</span></td>
                </tr>
              `).join('')}
              ${online === 0 ? '<tr><td colspan="7" class="text-muted" style="text-align:center;padding:24px;">No online clients</td></tr>' : ''}
            </tbody>
          </table>
        </div>
      </div>
    `;

    if (data.serverUptimeSeconds != null) {
      startTime = Date.now() - data.serverUptimeSeconds * 1000;
      updateUptime();
    }
  } catch (err) {
    container.innerHTML = `
      <div class="page-header"><h1 class="page-title">Dashboard</h1></div>
      <div class="card"><p class="text-muted">Failed to load data: ${err.message}</p></div>
    `;
  }
});

let uptimeInterval = null;
let startTime = Date.now();

function updateUptime() {
  const el = document.getElementById('uptime');
  if (!el) return;
  const seconds = Math.floor((Date.now() - startTime) / 1000);
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  el.textContent = `${d}d ${h}h ${m}m`;
}

document.addEventListener('DOMContentLoaded', () => {
  startTime = Date.now();
  setInterval(updateUptime, 10000);
});
