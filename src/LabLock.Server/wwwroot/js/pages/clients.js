let selectedClient = null;

Router.register('clients', async (container) => {
  const params = new URLSearchParams(window.location.hash.split('?')[1] || '');
  const clientFilter = params.get('client') || '';

  container.innerHTML = '<div class="loading">Loading clients...</div>';

  try {
    const res = await api('/api/clients');
    const data = await res.json();

    container.innerHTML = `
      <div class="page-header">
        <h1 class="page-title">Clients</h1>
        <span class="text-muted">${data.total} total, ${data.online} online</span>
      </div>
      <div class="card">
        <div class="table-container">
          <table>
            <thead>
              <tr>
                <th>Client ID</th>
                <th>Hostname</th>
                <th>IP</th>
                <th>User</th>
                <th>OS</th>
                <th>Agent</th>
                <th>CPU</th>
                <th>Memory</th>
                <th>Active App</th>
                <th>Status</th>
                <th>Action</th>
              </tr>
            </thead>
            <tbody>
              ${data.clients.map(c => `
                <tr>
                  <td><strong>${c.clientId}</strong></td>
                  <td>${c.hostname || '-'}</td>
                  <td class="font-mono text-sm">${c.ipAddresses || '-'}</td>
                  <td>${c.interactiveUser || c.currentUser || '-'}</td>
                  <td class="text-sm">${fmtOs(c.osVersion)}</td>
                  <td class="font-mono text-sm">${c.agentVersion || '-'}</td>
                  <td>${c.cpuPercent != null ? c.cpuPercent.toFixed(1) + '%' : '-'}</td>
                  <td>${c.memoryPercent != null ? c.memoryPercent.toFixed(1) + '%' : '-'}</td>
                  <td class="text-sm">${c.activeProcess || '-'}</td>
                  <td><span class="status-badge ${c.isOnline ? 'online' : 'offline'}">${c.isOnline ? 'Online' : 'Offline'}</span></td>
                  <td>
                    <button class="btn btn-sm ${c.isOnline ? 'btn-primary' : ''}" onclick="showClientModal('${c.clientId}')" ${!c.isOnline ? 'disabled' : ''}>
                      ${c.isOnline ? 'Command' : 'Offline'}
                    </button>
                    <button class="btn btn-sm" onclick="pushUpdate('${c.clientId}')" ${!c.isOnline ? 'disabled' : ''}>
                      Update
                    </button>
                  </td>
                </tr>
              `).join('')}
            </tbody>
          </table>
        </div>
      </div>
      <div class="card">
        <h2 class="card-title mb-4">Send Command</h2>
        <div class="form-group">
          <label>Target Client</label>
          <select id="cmdClient">
            ${data.clients.filter(c => c.isOnline).map(c => `
              <option value="${c.clientId}" ${c.clientId === clientFilter ? 'selected' : ''}>${c.clientId} (${c.hostname || 'no hostname'})</option>
            `).join('')}
          </select>
        </div>
        <div class="form-group">
          <label>PowerShell Command</label>
          <textarea id="cmdInput" rows="3" placeholder="Get-Process | Select-Object -First 10"></textarea>
        </div>
        <div style="display:flex;gap:8px;">
          <button class="btn btn-primary" onclick="sendCommand()">Execute</button>
          <button class="btn btn-sm" onclick="document.getElementById('cmdOutput').innerHTML = ''">Clear</button>
        </div>
        <div id="cmdOutput" class="mt-4" style="display:none;"></div>
      </div>
    `;

    if (clientFilter) {
      document.getElementById('cmdClient').value = clientFilter;
    }
  } catch (err) {
    container.innerHTML = `<div class="card"><p class="text-muted">Error: ${err.message}</p></div>`;
  }
});

async function sendCommand() {
  const clientId = document.getElementById('cmdClient').value;
  const command = document.getElementById('cmdInput').value.trim();
  if (!command) return showToast('Enter a command', 'error');

  const outputDiv = document.getElementById('cmdOutput');
  outputDiv.style.display = 'block';
  outputDiv.innerHTML = '<pre>Executing...</pre>';

  try {
    const res = await api(`/api/clients/${clientId}/command`, {
      method: 'POST',
      body: JSON.stringify({ command, timeoutSeconds: 60 })
    });

    const data = await res.json();
    outputDiv.innerHTML = `<pre style="background:var(--bg-primary);padding:16px;border-radius:8px;overflow-x:auto;">${escapeHtml(data.output || 'No output')}</pre>`;
    showToast('Command executed', 'success');
  } catch (err) {
    outputDiv.innerHTML = `<pre style="background:var(--bg-primary);padding:16px;border-radius:8px;color:var(--accent-danger);">Error: ${err.message}</pre>`;
  }
}

function showClientModal(clientId) {
  document.getElementById('cmdClient').value = clientId;
  document.getElementById('cmdInput').focus();
}

async function pushUpdate(clientId) {
  if (!confirm(`Push the latest agent update to ${clientId}? The agent will download it and restart automatically.`)) return;
  try {
    const res = await api(`/api/clients/${encodeURIComponent(clientId)}/update`, { method: 'POST' });
    const data = await res.json();
    if (res.ok) {
      showToast(`Update pushed to ${clientId} (v${data.version || '?'})`, 'success');
    } else {
      showToast(data.error || 'Update push failed', 'error');
    }
  } catch (err) {
    showToast('Update push failed: ' + err.message, 'error');
  }
}

function escapeHtml(str) {
  if (!str) return '';
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

function fmtOs(v) {
  if (!v) return '-';
  if (!v.startsWith('Microsoft Windows NT')) return v;
  const m = v.match(/Microsoft Windows NT \d+\.\d+\.(\d+)\.(\d+)/);
  if (!m) return v;
  const build = parseInt(m[1], 10);
  const family = build >= 22000 ? 'Windows 11'
    : build >= 10240 ? 'Windows 10'
    : build >= 9600 ? 'Windows 8.1'
    : build >= 9200 ? 'Windows 8'
    : build >= 7601 ? 'Windows 7 SP1'
    : 'Windows 7';
  return `${family} (Build ${m[1]}.${m[2]})`;
}
