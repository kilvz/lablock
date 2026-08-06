Router.register('logs', async (container) => {
  container.innerHTML = `
    <div class="page-header">
      <h1 class="page-title">Activity Logs</h1>
    </div>
    <div class="card">
      <div class="flex gap-4 items-center mb-4">
        <div style="flex:1;">
          <label class="text-sm text-muted">Client ID</label>
          <input type="text" id="logClient" placeholder="all" style="width:150px;">
        </div>
        <div>
          <label class="text-sm text-muted">Event Type</label>
          <select id="logEventType" style="width:150px;">
            <option value="all">All</option>
            <option value="keystroke">Keystroke</option>
            <option value="process_start">Process Start</option>
            <option value="process_stop">Process Stop</option>
            <option value="window_focus">Window Focus</option>
            <option value="login">Login</option>
            <option value="logoff">Logoff</option>
          </select>
        </div>
        <div>
          <label class="text-sm text-muted">Hours Back</label>
          <input type="number" id="logHours" value="1" min="1" max="168" style="width:80px;">
        </div>
        <div>
          <label class="text-sm text-muted">Search</label>
          <input type="text" id="logSearch" placeholder="keywords..." style="width:150px;">
        </div>
        <button class="btn btn-primary" onclick="searchLogs()" style="margin-top:16px;">Search</button>
      </div>
    </div>
    <div id="logResults">
      <div class="loading">Enter search criteria and click Search</div>
    </div>
  `;
});

async function searchLogs() {
  const container = document.getElementById('logResults');
  container.innerHTML = '<div class="loading">Loading...</div>';

  const clientId = document.getElementById('logClient').value || 'all';
  const eventType = document.getElementById('logEventType').value;
  const hoursBack = document.getElementById('logHours').value || 1;
  const search = document.getElementById('logSearch').value;

  try {
    const from = new Date(Date.now() - hoursBack * 3600000).toISOString();
    let url = `/api/logs?clientId=${clientId}&eventType=${eventType}&from=${from}&page=1&pageSize=100`;
    if (search) url += `&search=${encodeURIComponent(search)}`;

    const res = await api(url);
    const data = await res.json();

    if (data.logs.length === 0) {
      container.innerHTML = '<div class="card"><p class="text-muted">No logs found</p></div>';
      return;
    }

    container.innerHTML = `
      <div class="card">
        <div class="card-header">
          <h2 class="card-title">Results (${data.total} total)</h2>
        </div>
        <div class="table-container" style="max-height:60vh;overflow-y:auto;">
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Client</th>
                <th>Type</th>
                <th>Process</th>
                <th>Details</th>
              </tr>
            </thead>
            <tbody>
              ${data.logs.map(log => `
                <tr>
                  <td class="text-sm font-mono">${new Date(log.timestamp).toLocaleTimeString()}</td>
                  <td>${log.clientId}</td>
                  <td><span class="status-badge" style="color:var(--accent-info);background:rgba(52,152,219,0.1);">${log.eventType}</span></td>
                  <td class="text-sm">${log.processName || '-'}</td>
                  <td class="text-sm">${escapeHtml(log.details || '')}</td>
                </tr>
              `).join('')}
            </tbody>
          </table>
        </div>
      </div>
    `;
  } catch (err) {
    container.innerHTML = `<div class="card"><p class="text-muted">Error: ${err.message}</p></div>`;
  }
}
