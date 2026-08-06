Router.register('settings', async (container) => {
  const user = getUser();

  container.innerHTML = `
    <div class="page-header">
      <h1 class="page-title">Settings</h1>
    </div>
    <div class="card">
      <h2 class="card-title mb-4">Account</h2>
      <p class="text-muted">Logged in as <strong>${user?.username || 'admin'}</strong> (${user?.role || 'user'})</p>
    </div>
    <div class="card">
      <h2 class="card-title mb-4">AI Configuration</h2>
      <div class="flex gap-2">
        <button class="btn" onclick="Router.navigate('#ai')">AI Chat</button>
        <button class="btn" onclick="showAiSettings()">AI Settings</button>
      </div>
    </div>
    <div class="card">
      <h2 class="card-title mb-4">Server Info</h2>
      <div id="serverInfo">
        <div class="loading">Loading...</div>
      </div>
    </div>
    <div class="card">
      <h2 class="card-title mb-4">Danger Zone</h2>
      <button class="btn btn-danger" onclick="bootstrapAdmin()">Re-bootstrap Admin</button>
    </div>
  `;

  loadServerInfo();
});

async function loadServerInfo() {
  const infoEl = document.getElementById('serverInfo');
  try {
    const res = await api('/api/clients');
    const data = await res.json();
    infoEl.innerHTML = `
      <p class="text-muted">Total registered clients: ${data.total}</p>
      <p class="text-muted">Currently online: ${data.online}</p>
      <p class="text-muted mt-2">Server: LabLock v1.0</p>
    `;
  } catch {
    infoEl.innerHTML = '<p class="text-muted">Failed to load server info</p>';
  }
}

async function bootstrapAdmin() {
  if (!confirm('Reset admin password to default (admin/admin)?')) return;

  try {
    const res = await fetch('/api/auth/bootstrap', { method: 'POST' });
    if (res.ok) {
      showToast('Admin re-bootstrapped. Password: admin/admin', 'success');
    } else {
      const data = await res.json();
      showToast(data.error || 'Already bootstrapped', 'error');
    }
  } catch (err) {
    showToast(`Error: ${err.message}`, 'error');
  }
}
