/* A stand-in for the Windows host so the interface can be previewed in any browser.
   Open tools/ui-preview/dev.html (add ?scenario=working, prompt, paused, setup, light, connections). */
(() => {
  const listeners = [];
  const params = new URLSearchParams(location.search);
  const scenario = params.get('scenario') || 'empty';
  const emit = (type, data) => setTimeout(() => listeners.forEach(fn => fn({ data: { type, data: JSON.parse(JSON.stringify(data)) } })), 0);
  const now = () => Date.now();
  let seq = 0;
  const id = p => `${p}_${++seq}`;

  const providers = [
    { id: 'anthropic', name: 'Anthropic (Claude)', style: 'anthropic', baseUrl: 'https://api.anthropic.com', defaultModel: 'claude-sonnet-5-5', requiresKey: true, keyUrl: 'https://console.anthropic.com/settings/keys', suggestedModels: ['claude-sonnet-5-5', 'claude-opus-5-5', 'claude-haiku-5-5', 'claude-fable-5-1'], notes: 'Strong at operating apps from screenshots. Uses prompt caching to keep long tasks cheap.', allowStyleChoice: false },
    { id: 'openai', name: 'OpenAI', style: 'openai-responses', baseUrl: 'https://api.openai.com/v1', defaultModel: 'gpt-6.1-terra', requiresKey: true, keyUrl: 'https://platform.openai.com/api-keys', suggestedModels: ['gpt-6.1-terra', 'gpt-6-luna'], notes: 'Uses the Responses API. Switch to Chat Completions if you use an older proxy.', allowStyleChoice: true },
    { id: 'gemini', name: 'Google Gemini', style: 'openai-chat', baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai', defaultModel: 'gemini-3.8-flash', requiresKey: true, keyUrl: 'https://aistudio.google.com/apikey', suggestedModels: ['gemini-3.8-flash'], notes: "Uses Google's OpenAI-compatible endpoint.", allowStyleChoice: false },
    { id: 'openrouter', name: 'OpenRouter', style: 'openai-chat', baseUrl: 'https://openrouter.ai/api/v1', defaultModel: '', requiresKey: true, keyUrl: 'https://openrouter.ai/keys', suggestedModels: [], notes: 'One key for hundreds of models.', allowStyleChoice: false },
    { id: 'groq', name: 'Groq', style: 'openai-chat', baseUrl: 'https://api.groq.com/openai/v1', defaultModel: '', requiresKey: true, keyUrl: '', suggestedModels: [], notes: 'Very fast.', allowStyleChoice: false },
    { id: 'mistral', name: 'Mistral', style: 'openai-chat', baseUrl: 'https://api.mistral.ai/v1', defaultModel: '', requiresKey: true, keyUrl: '', suggestedModels: [], notes: '', allowStyleChoice: false },
    { id: 'xai', name: 'xAI (Grok)', style: 'openai-chat', baseUrl: 'https://api.x.ai/v1', defaultModel: '', requiresKey: true, keyUrl: '', suggestedModels: [], notes: '', allowStyleChoice: false },
    { id: 'ollama', name: 'Ollama (on this PC)', style: 'openai-chat', baseUrl: 'http://localhost:11434/v1', defaultModel: '', requiresKey: false, keyUrl: '', suggestedModels: [], notes: 'Runs locally, nothing leaves your PC.', allowStyleChoice: false },
    { id: 'lmstudio', name: 'LM Studio (on this PC)', style: 'openai-chat', baseUrl: 'http://localhost:1234/v1', defaultModel: '', requiresKey: false, keyUrl: '', suggestedModels: [], notes: '', allowStyleChoice: false },
    { id: 'custom', name: 'Custom (OpenAI-compatible)', style: 'openai-chat', baseUrl: '', defaultModel: '', requiresKey: false, keyUrl: '', suggestedModels: [], notes: 'Any compatible server.', allowStyleChoice: true },
  ];
  const providerSettings = {};
  for (const p of providers) providerSettings[p.id] = { baseUrl: '', model: '', apiStyle: '', priceInput: null, priceOutput: null, keySet: p.id === 'anthropic' && scenario !== 'setup', keyHint: '••••9f3a' };

  const state = {
    version: '1.0.0',
    setupComplete: scenario !== 'setup',
    control: { active: false, starting: false, expiresAt: null, autoStopMinutes: 20, port: 8765, mcpEnabled: true, mcpRunning: false, mcpError: null, localUrlMasked: null, publicUrlMasked: null },
    tunnel: { mode: 'off', status: 'off', error: null, progress: 0, cloudflaredFound: false, customUrl: '' },
    agent: { state: 'idle', running: false, paused: false, detail: '', provider: 'Anthropic (Claude)', model: 'claude-sonnet-5-5', notReady: scenario === 'setup' ? 'Add your Anthropic (Claude) API key in Settings → AI model.' : null, usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, cost: null, hasHistory: false },
    voice: { state: 'idle', talkKey: 'Right Ctrl', talkMode: 'hold', partial: null, lastHeard: null, micTest: false },
    prompts: [],
    approvals: {
      session: [],
      always: [{ key: 'exe:c:\\windows\\system32\\notepad.exe', label: 'notepad.exe', detail: 'c:\\windows\\system32\\notepad.exe' }],
      blocked: ['1password.exe', 'bitwarden.exe', 'keepass.exe', 'keepassxc.exe'].map(b => ({ key: b, label: b, detail: 'Any program with this file name' })),
    },
    settings: {
      schemaVersion: 1, setupComplete: scenario !== 'setup', providerId: 'anthropic', providers: providerSettings, maxSteps: 0, keepScreenshots: 3, maxOutputTokens: 4096, customInstructions: '',
      talkKey: 'RightCtrl', talkMode: 'hold', wakeWordEnabled: false, voiceCommandsEnabled: true, sttEngine: 'windows',
      transcription: { baseUrl: 'https://api.openai.com/v1', model: 'gpt-transcribe', voice: '', useProviderKey: true, keySet: false, keyHint: '' },
      language: 'auto', ttsEngine: 'windows', windowsVoice: '', speechRate: 1.0,
      speech: { baseUrl: 'https://api.openai.com/v1', model: 'gpt-4o-mini-tts', voice: 'alloy', useProviderKey: true, keySet: false, keyHint: '' },
      speakReplies: true, narrateSteps: true, autoStopMinutes: 20, confirmRiskySteps: true, riskyPhrases: 'send, send now, delete, remove, pay, buy, purchase, place order, submit, confirm, transfer, publish, post, share, senden, löschen, bezahlen, kaufen, bestellen',
      clickPreview: true, clickPreviewMs: 350, pauseOnUserInput: true, tripleEscStop: true, autoStartControlOnTalk: true, approvalTimeoutSeconds: 60,
      port: 8765, mcpServerEnabled: true, reuseSecret: false, tunnelMode: 'off', customTunnelUrl: '', cloudflaredPath: '',
      theme: scenario === 'light' ? 'light' : 'dark', bubbleMode: 'always', bubbleLeft: null, bubbleTop: null, startWithWindows: false, closeToTray: true, trayHintShown: false,
    },
    providers,
    talkKeys: [{ id: 'RightCtrl', label: 'Right Ctrl', hint: '' }, { id: 'CtrlSpace', label: 'Ctrl + Space', hint: '' }, { id: 'CtrlShiftSpace', label: 'Ctrl + Shift + Space', hint: '' }, { id: 'F8', label: 'F8', hint: '' }, { id: 'F9', label: 'F9', hint: '' }],
    platform: { osDescription: 'Microsoft Windows 10.0.26100', machineSummary: 'Windows 11, 1 monitor', voices: [{ id: 'v1', name: 'Microsoft Aria', language: 'en-US', gender: 'Female' }, { id: 'v2', name: 'Microsoft Katja', language: 'de-DE', gender: 'Female' }], recognizerLanguages: ['en-US', 'de-DE'], liveRecognizerAvailable: true, wakeWordAvailable: true, wakeWordError: null, microphoneAvailable: true, mcpLibraryAvailable: true },
    defaultRiskyPhrases: 'send, delete, pay',
  };

  const chat = [];
  const steps = [];
  const logs = [];
  const pushState = () => emit('state', state);
  const log = (category, message) => { const e = { time: now(), category, message }; logs.push(e); emit('log', e); };
  const addChat = (role, text, source, runId) => { const e = { id: id('msg'), role, text, time: now(), runId: runId || null, source: source || null }; chat.push(e); emit('chat', e); };

  function fakeShot(label, accent) {
    const c = document.createElement('canvas');
    c.width = 360; c.height = 220;
    const g = c.getContext('2d');
    g.fillStyle = '#f3f3f6'; g.fillRect(0, 0, 360, 220);
    g.fillStyle = '#ffffff'; g.fillRect(0, 0, 360, 26);
    g.fillStyle = '#d6d6de'; g.fillRect(0, 26, 360, 1);
    g.fillStyle = '#333'; g.font = '12px Segoe UI, sans-serif'; g.fillText(label, 10, 17);
    g.fillStyle = '#e7e7ee'; for (let i = 0; i < 6; i++) g.fillRect(14, 40 + i * 26, 220 - i * 18, 10);
    g.strokeStyle = accent; g.lineWidth = 2; g.strokeRect(250, 160, 90, 34);
    g.fillStyle = accent; g.fillRect(250, 146, 18, 14);
    g.fillStyle = '#fff'; g.font = 'bold 10px sans-serif'; g.fillText('7', 256, 157);
    return c.toDataURL('image/jpeg', 0.7);
  }

  function step(runId, index, tool, title, status, detail, ms, thumb) {
    const s = { id: id('step'), runId, index, tool, title, source: 'assistant', status, detail, thumb: thumb || null, time: now() + index, ms };
    steps.push(s); emit('step', s);
    return s;
  }

  function startControl() {
    state.control.active = true;
    state.control.expiresAt = now() + 20 * 60000 - 1000 * 77;
    state.control.mcpRunning = true;
    state.control.localUrlMasked = 'http://127.0.0.1:8765/k/3fa9••••••••c21e/mcp';
    log('system', 'Desktop control is ON (You turned it on).');
  }

  let pendingAnswer = null;
  async function runTask(text, source) {
    addChat('user', text, source);
    if (!state.control.active) startControl();
    const runId = id('run');
    state.agent.running = true; state.agent.state = 'thinking'; pushState();
    await wait(700);
    addChat('assistant', 'Opening Notepad and writing the list.', 'narration', runId);
    state.agent.state = 'acting'; state.agent.detail = 'Open Notepad'; pushState();
    const s1 = step(runId, 1, 'windows_open_app', 'Open Notepad', 'running', null, 0);
    await wait(500);
    s1.status = 'done'; s1.detail = 'Opened Notepad'; s1.ms = 640; emit('step', s1);
    const s2 = step(runId, 2, 'windows_screenshot', 'Screenshot the window', 'done', 'Screenshot of notepad.exe', 412, fakeShot('Untitled - Notepad', '#7c6cff'));
    const s3 = step(runId, 3, 'windows_type', 'Type 182 characters', 'running', null, 0);
    state.prompts = [{ id: 'prompt_1', kind: 'app', title: 'Allow the assistant to use notepad.exe?', detail: 'C:\\Windows\\System32\\notepad.exe\nUntitled - Notepad', appName: 'notepad.exe',
      choices: [{ id: 'session', label: 'Allow this session', style: 'primary' }, { id: 'always', label: 'Always allow', style: 'default' }, { id: 'deny', label: 'Deny', style: 'default' }, { id: 'block', label: 'Never allow', style: 'danger' }], expiresAt: now() + 60000 }];
    pushState();
    if (scenario === 'prompt') return;
    const answer = await new Promise(r => { pendingAnswer = r; if (scenario === 'working' || scenario === 'paused') setTimeout(() => r('session'), 300); });
    state.prompts = [];
    if (answer === 'session') state.approvals.session.push({ key: 'exe:notepad', label: 'notepad.exe', detail: 'C:\\Windows\\System32\\notepad.exe' });
    pushState();
    s3.status = 'done'; s3.detail = 'Typed 182 characters in notepad.exe'; s3.ms = 2210; emit('step', s3);
    const s4 = step(runId, 4, 'windows_key', 'Press Ctrl+S', 'done', 'Pressed Ctrl+S in notepad.exe', 96);
    const s5 = step(runId, 5, 'windows_screenshot', 'Screenshot the window', 'done', 'Screenshot of notepad.exe', 388, fakeShot('Save as', '#2fd6ee'));
    state.agent.usage = { input: 18200, output: 940, cacheRead: 64100, cacheWrite: 5200 }; state.agent.cost = 0.0612;
    if (scenario === 'paused') {
      state.agent.paused = true; state.agent.detail = 'Paused — you took over. Press Resume or say “continue” when you’re done.'; state.agent.state = 'paused';
      step(runId, 6, 'windows_tap', 'Click element 12', 'interrupted', 'Interrupted: the user took over.', 120);
      pushState();
      return;
    }
    if (scenario === 'working') { step(runId, 6, 'windows_tap', 'Click element 12', 'running', null, 0); pushState(); return; }
    await wait(500);
    state.agent.running = false; state.agent.state = 'idle'; state.agent.detail = 'Done';
    addChat('assistant', 'Done. The packing list is in Notepad and saved as packing-list.txt in Documents.', 'final', runId);
    state.voice.state = 'speaking'; pushState();
    await wait(1600);
    state.voice.state = 'idle'; pushState();
  }

  const wait = ms => new Promise(r => setTimeout(r, ms));

  function merge(target, patch) {
    for (const [k, v] of Object.entries(patch)) {
      if (v && typeof v === 'object' && !Array.isArray(v) && target[k] && typeof target[k] === 'object') merge(target[k], v);
      else target[k] = v;
    }
  }

  let listenTimer = null;
  function handle(msg) {
    switch (msg.type) {
      case 'ready':
        emit('history', { chat, steps, logs });
        pushState();
        break;
      case 'control.toggle':
        if (state.control.active) { state.control.active = false; state.control.mcpRunning = false; state.control.localUrlMasked = null; log('system', 'Desktop control is OFF.'); }
        else startControl();
        pushState();
        break;
      case 'control.extend': state.control.expiresAt += msg.minutes * 60000; pushState(); break;
      case 'chat.send': runTask(msg.text, 'typed'); break;
      case 'chat.new': chat.length = 0; emit('cleared', {}); break;
      case 'agent.pause': state.agent.paused = true; state.agent.detail = 'Paused. Press Resume to continue.'; pushState(); break;
      case 'agent.resume': state.agent.paused = false; pushState(); break;
      case 'agent.stop': state.agent.running = false; state.agent.paused = false; addChat('system', 'Stopped.'); pushState(); break;
      case 'prompt.answer': if (pendingAnswer) pendingAnswer(msg.choice); break;
      case 'voice.toggle':
        if (state.voice.state === 'listening') { clearInterval(listenTimer); state.voice.state = 'transcribing'; pushState(); setTimeout(() => { state.voice.state = 'idle'; pushState(); runTask('Open Notepad and write a packing list for a weekend trip', 'voice'); }, 600); }
        else { state.voice.state = 'listening'; pushState(); let t = 0; listenTimer = setInterval(() => emit('level', { value: Math.abs(Math.sin((t += 0.35))) * 0.8 }), 70); }
        break;
      case 'voice.micTest':
        clearInterval(listenTimer);
        if (msg.on) { let t = 0; listenTimer = setInterval(() => emit('level', { value: Math.abs(Math.sin((t += 0.3))) * 0.7 }), 70); }
        break;
      case 'voice.testListen': setTimeout(() => emit('transcript', { text: 'This is a test of the microphone.' }), 900); break;
      case 'settings.update': merge(state.settings, msg.patch); if (msg.patch.tunnelMode) state.tunnel.mode = msg.patch.tunnelMode; pushState(); break;
      case 'settings.setKey': {
        const t = msg.target;
        if (t.startsWith('provider:')) { const p = state.settings.providers[t.slice(9)]; p.keySet = !!msg.key; p.keyHint = msg.key ? '••••' + msg.key.slice(-4) : ''; state.agent.notReady = null; }
        emit('toast', { kind: 'success', message: msg.key ? 'Key saved (encrypted for your Windows account).' : 'Key removed.' });
        pushState();
        break;
      }
      case 'models.load': setTimeout(() => emit('models', { providerId: msg.providerId, models: ['claude-sonnet-5-5', 'claude-opus-5-5', 'claude-haiku-5-5', 'claude-fable-5-1'], error: null }), 500); break;
      case 'provider.test': setTimeout(() => emit('providerTest', { providerId: msg.providerId, ok: true, message: 'Connected to claude-sonnet-5-5 in 812 ms.', reply: 'OK' }), 600); break;
      case 'secret.copy': emit('toast', { kind: 'success', message: 'Copied. Keep it private — it controls this PC.' }); break;
      case 'secret.reveal': emit('secret', { local: 'http://127.0.0.1:8765/k/3fa94c0d2b6e8a1f0c9d7e5b3a1f2e4d6c8b0a9f7e5d3c1b2a4f6e8d0c21e/mcp', public: '' }); break;
      case 'approvals.revoke': for (const k of ['session', 'always']) state.approvals[k] = state.approvals[k].filter(e => e.key !== msg.key); pushState(); break;
      case 'approvals.unblock': state.approvals.blocked = state.approvals.blocked.filter(e => e.key !== msg.key); pushState(); break;
      case 'approvals.block': state.approvals.blocked.push({ key: msg.name, label: msg.name, detail: 'Any program with this file name' }); pushState(); break;
      case 'tunnel.download': state.tunnel.status = 'downloading'; pushState(); { let p = 0; const t = setInterval(() => { p += 0.2; state.tunnel.progress = Math.min(1, p); if (p >= 1) { clearInterval(t); state.tunnel.status = 'off'; state.tunnel.cloudflaredFound = true; } pushState(); }, 250); } break;
      case 'tunnel.start': state.tunnel.status = 'online'; state.control.publicUrlMasked = 'https://brave-otter-42.trycloudflare.com/k/3fa9••••••••c21e/mcp'; pushState(); break;
      case 'tunnel.stop': state.tunnel.status = 'off'; state.control.publicUrlMasked = null; pushState(); break;
      case 'setup.complete': state.setupComplete = true; state.settings.setupComplete = true; pushState(); break;
      default: break;
    }
  }

  window.chrome = window.chrome || {};
  window.chrome.webview = {
    postMessage: msg => handle(JSON.parse(JSON.stringify(msg))),
    addEventListener: (name, fn) => { if (name === 'message') listeners.push(fn); },
  };

  // Seed scenarios.
  log('system', 'Ready. Desktop control is off until you start it or ask the assistant to do something.');
  if (['working', 'prompt', 'paused'].includes(scenario)) setTimeout(() => runTask('Open Notepad and write a packing list for a weekend trip', 'voice'), 50);
  if (scenario === 'listening') setTimeout(() => handle({ type: 'voice.toggle' }), 50);
  if (scenario === 'connections') { startControl(); state.tunnel.mode = 'cloudflare'; state.settings.tunnelMode = 'cloudflare'; state.tunnel.cloudflaredFound = true; state.tunnel.status = 'online'; state.control.publicUrlMasked = 'https://brave-otter-42.trycloudflare.com/k/3fa9••••••••c21e/mcp'; }
  if (scenario === 'activity') { for (const [c, m] of [['action', 'Read 84 elements in chrome.exe'], ['approval', 'Allowed this session: chrome.exe'], ['action', 'Clicked “Compose” in chrome.exe'], ['voice', 'Heard the wake word.'], ['error', 'Port 8765 is already in use, so other assistants can\'t connect.'], ['mcp', 'Refused a request without the valid secret']]) log(c, m); }
})();
