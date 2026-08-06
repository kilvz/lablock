let connection = null;
let connectionState = { connected: false, clientCount: 0 };

function getToken() {
  return localStorage.getItem('lablock_token');
}

function getUser() {
  try {
    return JSON.parse(localStorage.getItem('lablock_user'));
  } catch {
    return null;
  }
}

function checkAuth() {
  const token = getToken();
  if (!token) {
    window.location.href = '/login.html';
    return false;
  }
  return true;
}

function showToast(message, type = 'info') {
  const toast = document.createElement('div');
  toast.className = `toast ${type}`;
  toast.textContent = message;
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), 3000);
}

async function api(path, options = {}) {
  const token = getToken();
  const headers = { ...options.headers };

  if (token) headers['Authorization'] = `Bearer ${token}`;

  if (!(options.body instanceof FormData)) {
    headers['Content-Type'] = 'application/json';
  }

  const res = await fetch(path, { ...options, headers });

  if (res.status === 401) {
    localStorage.removeItem('lablock_token');
    localStorage.removeItem('lablock_user');
    window.location.href = '/login.html';
    throw new Error('Unauthorized');
  }

  return res;
}

function connectSignalR() {
  if (connection) return;

  const token = getToken();
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/client', {
      accessTokenFactory: () => token
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();

  connection.on('ClientConnected', (data) => {
    connectionState.clientCount++;
    if (typeof onClientConnected === 'function') onClientConnected(data);
  });

  connection.on('ClientDisconnected', (data) => {
    connectionState.clientCount = Math.max(0, connectionState.clientCount - 1);
    if (typeof onClientDisconnected === 'function') onClientDisconnected(data);
  });

  connection.on('CommandExecuted', (data) => {
    if (typeof onCommandExecuted === 'function') onCommandExecuted(data);
  });

  connection.onreconnecting(() => {
    connectionState.connected = false;
    if (typeof onConnectionChange === 'function') onConnectionChange(false);
  });

  connection.onreconnected(() => {
    connectionState.connected = true;
    if (typeof onConnectionChange === 'function') onConnectionChange(true);
  });

  connection.onclose(() => {
    connectionState.connected = false;
    if (typeof onConnectionChange === 'function') onConnectionChange(false);
  });

  connection.start().then(() => {
    connectionState.connected = true;
    if (typeof onConnectionChange === 'function') onConnectionChange(true);
  }).catch(() => {});
}

function logout() {
  localStorage.removeItem('lablock_token');
  localStorage.removeItem('lablock_user');
  if (connection) {
    connection.stop();
    connection = null;
  }
  window.location.href = '/login.html';
}

document.addEventListener('DOMContentLoaded', () => {
  if (!checkAuth()) return;

  const user = getUser();
  if (user) {
    const nameEl = document.getElementById('userName');
    if (nameEl) nameEl.textContent = user.displayName || user.username;
  }

  connectSignalR();
  Router.init();
});
