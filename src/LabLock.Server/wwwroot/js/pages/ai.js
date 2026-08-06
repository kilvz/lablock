let currentConversation = null;
let isStreaming = false;

Router.register('ai', async (container) => {
  container.innerHTML = `
    <div class="page-header">
      <h1 class="page-title">AI Assistant</h1>
      <div class="flex gap-2">
        <button class="btn btn-sm" onclick="newConversation()">New Chat</button>
        <button class="btn btn-sm" id="aiSettingsBtn" onclick="showAiSettings()">Settings</button>
      </div>
    </div>
    <div id="aiStatus" class="mb-4"></div>
    <div class="chat-container">
      <div class="chat-messages" id="chatMessages">
        <div class="chat-message assistant">
          <p>Hello! I'm LabLock AI. I can help you manage your lab PCs. Try asking:</p>
          <ul style="margin-top:8px;padding-left:20px;">
            <li>"Show me all online clients"</li>
            <li>"Get system info for PC-LAB-001"</li>
            <li>"Run Get-Process on all PCs"</li>
            <li>"Who's logged into PC-LAB-003?"</li>
          </ul>
        </div>
      </div>
      <div class="chat-input">
        <input type="text" id="chatInput" placeholder="Type a message..." onkeydown="if(event.key==='Enter'&&!event.shiftKey){event.preventDefault();sendChatMessage();}">
        <button class="btn btn-primary" onclick="sendChatMessage()" id="chatSendBtn">Send</button>
      </div>
    </div>
  `;

  checkAiStatus();
});

async function checkAiStatus() {
  const statusEl = document.getElementById('aiStatus');
  try {
    const res = await api('/api/ai/status');
    const data = await res.json();

    if (data.configured) {
      statusEl.innerHTML = `<span class="text-sm text-muted">AI: ${data.config.provider} / ${data.config.model}</span>`;
    } else {
      statusEl.innerHTML = `
        <div class="flex items-center gap-2" style="color:var(--accent-warning);">
          <span>AI not configured</span>
          <button class="btn btn-sm" onclick="showAiSettings()">Configure</button>
        </div>
      `;
    }
  } catch {
    statusEl.innerHTML = '';
  }
}

async function startConversation() {
  try {
    const res = await api('/api/ai/conversations', { method: 'POST' });
    const data = await res.json();
    currentConversation = data.id;
  } catch (err) {
    showToast('Failed to create conversation', 'error');
  }
}

async function sendChatMessage() {
  if (isStreaming) return;

  const input = document.getElementById('chatInput');
  const message = input.value.trim();
  if (!message) return;

  if (!currentConversation) await startConversation();
  if (!currentConversation) return;

  input.value = '';
  isStreaming = true;
  document.getElementById('chatSendBtn').disabled = true;

  const messagesDiv = document.getElementById('chatMessages');

  const userMsg = document.createElement('div');
  userMsg.className = 'chat-message user';
  userMsg.textContent = message;
  messagesDiv.appendChild(userMsg);

  const assistantMsg = document.createElement('div');
  assistantMsg.className = 'chat-message assistant';
  assistantMsg.innerHTML = '<em>Thinking...</em>';
  messagesDiv.appendChild(assistantMsg);
  messagesDiv.scrollTop = messagesDiv.scrollHeight;

  const toolResults = [];

  try {
    const res = await fetch(`/api/ai/chat/${currentConversation}`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${getToken()}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ message })
    });

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let fullText = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() || '';

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const json = line.slice(6);
        if (!json) continue;

        try {
          const evt = JSON.parse(json);

          if (evt.type === 'text') {
            fullText += evt.content;
            assistantMsg.innerHTML = renderMarkdown(fullText);
            messagesDiv.scrollTop = messagesDiv.scrollHeight;
          } else if (evt.type === 'tool_call') {
            const toolMsg = document.createElement('div');
            toolMsg.className = 'chat-message assistant';
            toolMsg.style.fontSize = '12px';
            toolMsg.style.color = 'var(--text-muted)';
            toolMsg.textContent = `🔧 Using tool: ${evt.toolName}(${evt.toolArgs})`;
            messagesDiv.appendChild(toolMsg);
          } else if (evt.type === 'tool_executing') {
            assistantMsg.innerHTML = fullText + '\n\n<em>Executing tool...</em>';
          } else if (evt.type === 'tool_result') {
            const resultMsg = document.createElement('div');
            resultMsg.className = 'chat-message assistant';
            resultMsg.style.fontSize = '12px';
            resultMsg.style.background = 'rgba(0,255,136,0.05)';
            resultMsg.style.border = '1px solid rgba(0,255,136,0.2)';
            resultMsg.innerHTML = `<strong>Result:</strong>\n<pre>${escapeHtml(evt.content)}</pre>`;
            messagesDiv.appendChild(resultMsg);
          } else if (evt.type === 'error') {
            assistantMsg.innerHTML = `<span style="color:var(--accent-danger)">Error: ${evt.content}</span>`;
          }
        } catch {}
      }
    }

    if (!fullText) {
      assistantMsg.innerHTML = '<span class="text-muted">No response received</span>';
    }
  } catch (err) {
    assistantMsg.innerHTML = `<span style="color:var(--accent-danger)">Connection error: ${err.message}</span>`;
  }

  isStreaming = false;
  document.getElementById('chatSendBtn').disabled = false;
  messagesDiv.scrollTop = messagesDiv.scrollHeight;
}

async function newConversation() {
  currentConversation = null;
  const messagesDiv = document.getElementById('chatMessages');
  if (messagesDiv) {
    messagesDiv.innerHTML = `
      <div class="chat-message assistant">
        <p>New conversation started. How can I help you?</p>
      </div>
    `;
  }
}

async function showAiSettings() {
  const container = document.getElementById('content');
  container.innerHTML = '<div class="loading">Loading settings...</div>';

  try {
    const [settingsRes, providersRes] = await Promise.all([
      api('/api/ai/settings'),
      api('/api/ai/settings/providers')
    ]);

    const settings = await settingsRes.json();
    const providers = await providersRes.json();

    container.innerHTML = `
      <div class="page-header">
        <h1 class="page-title">AI Settings</h1>
        <button class="btn" onclick="Router.navigate(window.location.hash)">Back</button>
      </div>
      <div class="card">
        <form id="aiSettingsForm" onsubmit="saveAiSettings(event)">
          <div class="form-group">
            <label>Provider</label>
            <select id="aiProvider" onchange="updateModels()">
              ${providers.providers.map(p => `<option value="${p.id}" ${p.id === settings.provider ? 'selected' : ''}>${p.name}</option>`).join('')}
            </select>
          </div>
          <div class="form-group">
            <label>Model</label>
            <select id="aiModel">
              ${getModelsForProvider(settings.provider, settings.model).join('')}
            </select>
          </div>
          <div class="form-group">
            <label>API Key</label>
            <input type="password" id="aiApiKey" value="${settings.apiKey || ''}" placeholder="sk-...">
          </div>
          <div class="form-group">
            <label>Base URL</label>
            <input type="text" id="aiBaseUrl" value="${settings.baseUrl || ''}" placeholder="https://api.openai.com">
          </div>
          <div class="grid-2">
            <div class="form-group">
              <label>Temperature</label>
              <input type="number" id="aiTemperature" value="${settings.temperature || 0.3}" step="0.1" min="0" max="2">
            </div>
            <div class="form-group">
              <label>Max Tokens</label>
              <input type="number" id="aiMaxTokens" value="${settings.maxTokens || 4096}" step="256" min="256" max="128000">
            </div>
          </div>
          <div class="form-group">
            <label>System Prompt</label>
            <textarea id="aiSystemPrompt" rows="6" placeholder="Custom system prompt...">${settings.systemPrompt || ''}</textarea>
          </div>
          <button type="submit" class="btn btn-primary">Save Settings</button>
        </form>
      </div>
    `;
  } catch (err) {
    container.innerHTML = `<div class="card"><p class="text-muted">Error: ${err.message}</p></div>`;
  }
}

function getModelsForProvider(provider, selected) {
  const models = {
    openai: ['gpt-4o', 'gpt-4o-mini', 'gpt-4-turbo', 'gpt-3.5-turbo'],
    anthropic: ['claude-sonnet-4-20250514', 'claude-3-5-haiku-20241022', 'claude-opus-4-20250514'],
    gemini: ['gemini-2.5-pro', 'gemini-2.5-flash', 'gemini-2.0-flash'],
    ollama: [],
    custom: [],
    none: []
  };
  const list = models[provider] || [];
  if (list.length === 0) {
    return ['<option value="">Custom input (see Base URL)</option>'];
  }
  return list.map(m => `<option value="${m}" ${m === selected ? 'selected' : ''}>${m}</option>`);
}

function updateModels() {
  const provider = document.getElementById('aiProvider').value;
  const select = document.getElementById('aiModel');
  select.innerHTML = getModelsForProvider(provider, '').join('');
}

async function saveAiSettings(e) {
  e.preventDefault();
  const body = {
    provider: document.getElementById('aiProvider').value,
    model: document.getElementById('aiModel').value,
    apiKey: document.getElementById('aiApiKey').value,
    baseUrl: document.getElementById('aiBaseUrl').value,
    temperature: parseFloat(document.getElementById('aiTemperature').value) || 0.3,
    maxTokens: parseInt(document.getElementById('aiMaxTokens').value) || 4096,
    systemPrompt: document.getElementById('aiSystemPrompt').value
  };

  try {
    const res = await api('/api/ai/settings', {
      method: 'PUT',
      body: JSON.stringify(body)
    });
    const data = await res.json();
    showToast('Settings saved', 'success');
    if (data.configured) {
      setTimeout(() => Router.navigate('#ai'), 500);
    }
  } catch (err) {
    showToast(`Error: ${err.message}`, 'error');
  }
}

function renderMarkdown(text) {
  if (!text) return '';
  let html = text
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/```(\w*)\n([\s\S]*?)```/g, '<pre>$2</pre>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/\*(.+?)\*/g, '<em>$1</em>')
    .replace(/^- (.+)/gm, '<li>$1</li>')
    .replace(/\n/g, '<br>');
  return html;
}
