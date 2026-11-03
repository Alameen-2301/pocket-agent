/* Pocket Agent — interface logic.
   Talks to the Windows host through WebView2 postMessage. All dynamic text goes through
   textContent (never innerHTML), and frequent state updates never overwrite a field you're typing in. */
'use strict';
(() => {
  // ------------------------------------------------------------------ bridge
  const webview = window.chrome && window.chrome.webview;
  const send = (type, payload = {}) => { if (webview) webview.postMessage(Object.assign({ type }, payload)); };

  // ------------------------------------------------------------------ helpers
  const SVG = 'http://www.w3.org/2000/svg';
  const ICONS = {
    spark: '<path d="M12 3.5l1.9 5.3 5.3 1.9-5.3 1.9L12 18l-1.9-5.4-5.3-1.9 5.3-1.9z"/><path d="M19 3.5v3M17.5 5h3"/>',
    pulse: '<path d="M3 12h4l2.2-6 4.6 12 2.2-6h5"/>',
    plug: '<path d="M9 3v5M15 3v5M6.5 8h11v3.5a5.5 5.5 0 0 1-11 0zM12 17v4"/>',
    shield: '<path d="M12 3l7.5 3v6c0 4.3-3.2 8-7.5 9-4.3-1-7.5-4.7-7.5-9V6z"/><path d="M9 12l2 2 4-4"/>',
    sliders: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
    mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21"/>',
    send: '<path d="M12 19V5M5.5 11.5L12 5l6.5 6.5"/>',
    stop: '<rect x="6.5" y="6.5" width="11" height="11" rx="2"/>',
    pause: '<path d="M9 5.5v13M15 5.5v13"/>',
    play: '<path d="M8 5.5l11 6.5-11 6.5z"/>',
    copy: '<rect x="9" y="9" width="11" height="11" rx="2.5"/><path d="M5 15V6a2 2 0 0 1 2-2h8"/>',
    eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/>',
    refresh: '<path d="M20 11.5a8 8 0 1 0-2.4 5.7M20 4.5v7h-7"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    x: '<path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/>',
    warn: '<path d="M12 4l9 16H3z"/><path d="M12 10v4.5M12 17.5v.01"/>',
    lock: '<rect x="5" y="11" width="14" height="9.5" rx="2.5"/><path d="M8.5 11V8a3.5 3.5 0 0 1 7 0v3"/>',
    external: '<path d="M14 4.5h5.5V10M19.5 4.5l-8.5 8.5M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    trash: '<path d="M4.5 7h15M10 11v6M14 11v6M6.5 7l1 13h9l1-13M9.5 7V4.5h5V7"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l8.5-8.5M16.5 6.5l2.5 2.5"/>',
    globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5a13 13 0 0 1 0 17M12 3.5a13 13 0 0 0 0 17"/>',
    download: '<path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/>',
    power: '<path d="M12 3.5V11M6.6 6.6a7.5 7.5 0 1 0 10.8 0"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
    volume: '<path d="M4 9.5v5h3.5L12 18V6L7.5 9.5zM15.5 9.5a3.5 3.5 0 0 1 0 5M18.5 6.5a7.5 7.5 0 0 1 0 11"/>',
    info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5M12 8v.01"/>',
    compose: '<path d="M4.5 19.5h4l10-10-4-4-10 10zM13 7l4 4"/>',
    hand: '<path d="M8 13V6.5a1.5 1.5 0 0 1 3 0V11M11 10V5a1.5 1.5 0 0 1 3 0v6M14 10.5V7a1.5 1.5 0 0 1 3 0v6.5c0 4-2.7 7-6.5 7-2.5 0-4.2-1.3-5.6-3.4L4 14.5a1.5 1.5 0 0 1 2.4-1.8L8 14.5"/>',
    cloud: '<path d="M7 18.5h10a4 4 0 0 0 .6-8 5.5 5.5 0 0 0-10.6-1A4.5 4.5 0 0 0 7 18.5z"/>',
  };

  function icon(name, cls = '') {
    const el = document.createElementNS(SVG, 'svg');
    el.setAttribute('viewBox', '0 0 24 24');
    el.setAttribute('class', ('icon ' + cls).trim());
    el.setAttribute('aria-hidden', 'true');
    el.innerHTML = ICONS[name] || '';
    return el;
  }

  /** h('div.cls#id', {attrs/props/on*}, ...children) */
  function h(spec, props, ...children) {
    const m = /^([a-z0-9-]+)?((?:[.#][\w-]+)*)$/i.exec(spec) || [];
    const el = document.createElement(m[1] || 'div');
    (m[2] || '').replace(/([.#])([\w-]+)/g, (_, kind, name) => {
      if (kind === '.') el.classList.add(name); else el.id = name;
      return '';
    });
    if (props && (typeof props !== 'object' || props instanceof Node || Array.isArray(props))) { children.unshift(props); props = null; }
    for (const [key, value] of Object.entries(props || {})) {
      if (value === undefined || value === null || value === false) continue;
      if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
      else if (key === 'text') el.textContent = value;
      else if (key === 'class') el.className = value;
      else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
      else if (key in el && typeof value !== 'string') el[key] = value;
      else el.setAttribute(key, value === true ? '' : String(value));
    }
    append(el, children);
    return el;
  }

  function append(el, children) {
    for (const child of children.flat(Infinity)) {
      if (child === null || child === undefined || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  /** Appends children (nodes, strings, arrays; null/false skipped) — use instead of Element.append. */
  const put = (el, ...children) => append(el, children);
  const $ = (sel, root = document) => root.querySelector(sel);
  const clear = el => { while (el.firstChild) el.removeChild(el.firstChild); return el; };
  const setText = (el, text) => { if (el && el.textContent !== text) el.textContent = text; };
  const show = (el, visible) => { if (el) el.hidden = !visible; };
  const get = (obj, path) => path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
  const patchFor = (path, value) => path.split('.').reduceRight((acc, k) => ({ [k]: acc }), value);
  const fmtTime = ms => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  const fmtClock = ms => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const fmtTokens = n => (n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n || 0));
  const fmtMs = ms => (ms >= 1000 ? (ms / 1000).toFixed(ms >= 10000 ? 0 : 1) + ' s' : ms + ' ms');
  const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
  const languageName = tag => { try { return new Intl.DisplayNames(['en'], { type: 'language' }).of(tag) || tag; } catch { return tag; } };

  // ------------------------------------------------------------------ app state
  const app = {
    state: null,
    chat: [],
    steps: new Map(),
    logs: [],
    page: 'assistant',
    models: {},
    providerTests: {},
    transcript: null,
    level: 0,
    logFilter: 'all',
    logSearch: '',
    setupOpen: false,
    setupStep: 0,
    setupDismissed: false,
    revealed: null,
    builtProviderId: null,
  };
  const binders = [];
  /** Registers an updater for a control; it is dropped once the control leaves the page. */
  const bind = (el, fn) => { fn.el = el; binders.push(fn); return el; };

  // ------------------------------------------------------------------ toasts and lightbox
  function toast(kind, message) {
    const box = $('#toasts');
    const el = h('div.toast.' + (kind || 'info'), icon(kind === 'success' ? 'check' : kind === 'error' ? 'warn' : 'info', 'sm'), h('div', { text: message }));
    put(box, el);
    setTimeout(() => el.remove(), kind === 'error' ? 6000 : 3500);
    while (box.children.length > 4) box.firstChild.remove();
  }

  function lightbox(src) {
    const el = h('div.lightbox', { role: 'dialog', 'aria-label': 'Screenshot', onclick: () => el.remove() }, h('img', { src, alt: 'Screenshot from a step' }));
    put(document.body, el);
  }

  // ------------------------------------------------------------------ shell: rail and status bar
  const PAGES = [
    { id: 'assistant', label: 'Assistant', icon: 'spark', title: 'Assistant' },
    { id: 'activity', label: 'Activity', icon: 'pulse', title: 'Activity' },
    { id: 'connections', label: 'Connections', icon: 'plug', title: 'Connect other assistants' },
    { id: 'approvals', label: 'Approvals', icon: 'shield', title: 'Which apps it may use' },
    { id: 'settings', label: 'Settings', icon: 'sliders', title: 'Settings' },
  ];
  const nav = {};
  const bar = {};

  function buildShell() {
    const rail = $('#rail');
    put(rail, h('div.brand', h('img', { src: 'icon.png', alt: '' }), h('b', { text: 'Pocket Agent' })));
    for (const p of PAGES) {
      nav[p.id] = h('button.nav-item', { type: 'button', onclick: () => go(p.id), title: p.label }, icon(p.icon), h('span', { text: p.label }));
      put(rail, nav[p.id]);
    }
    nav.approvalsCount = h('span.count', { hidden: true });
    put(nav.assistant, nav.approvalsCount);
    bar.model = h('b');
    bar.version = h('span');
    put(rail, h('div.rail-foot', h('div.who', icon('spark', 'sm'), bar.model), bar.version));

    const sb = $('#statusbar');
    bar.title = h('h1');
    bar.dot = h('span.dot');
    bar.label = h('span.label');
    bar.left = h('span.left');
    bar.extend = h('button.btn.small.ghost', { type: 'button', text: '+10 min', title: 'Keep control on 10 minutes longer', onclick: () => send('control.extend', { minutes: 10 }) });
    bar.toggle = h('button.btn.small', { type: 'button', onclick: () => send('control.toggle') });
    bar.control = h('div.control', bar.dot, bar.label, bar.left, bar.extend, bar.toggle);
    put(sb, bar.title, h('div.spacer'), bar.control);
  }

  function go(page) {
    app.page = page;
    for (const p of PAGES) {
      nav[p.id].setAttribute('aria-current', p.id === page ? 'page' : 'false');
      show($('#page-' + p.id), p.id === page);
    }
    setText(bar.title, PAGES.find(p => p.id === page).title);
    if (page === 'assistant') setTimeout(() => composer.input && composer.input.focus(), 0);
    if (page !== 'settings' && app.micTestFromSettings) { send('voice.micTest', { on: false }); app.micTestFromSettings = false; }
    render();
  }

  function renderStatusBar(st) {
    const c = st.control;
    bar.control.dataset.on = String(!!c.active);
    setText(bar.label, c.starting ? 'Starting…' : c.active ? 'Control is on' : 'Control is off');
    show(bar.left, c.active);
    show(bar.extend, c.active && !!c.expiresAt);
    bar.toggle.className = 'btn small ' + (c.active ? 'stop' : 'primary');
    setText(bar.toggle, c.active ? 'Stop control' : 'Start control');
    bar.toggle.title = c.active ? 'Switch desktop control off now' : 'Let the assistant and connected apps use this PC';
    tickCountdown();
    setText(bar.model, st.agent.model || st.agent.provider || 'No model');
    setText(bar.version, 'Version ' + (st.version || ''));
    const pending = (st.prompts || []).length;
    show(nav.approvalsCount, pending > 0);
    setText(nav.approvalsCount, String(pending));
  }

  function tickCountdown() {
    const st = app.state;
    if (!st || !st.control.active) return;
    if (!st.control.expiresAt) { setText(bar.left, 'no time limit'); return; }
    const left = Math.max(0, st.control.expiresAt - Date.now());
    const m = Math.floor(left / 60000), s = Math.floor((left % 60000) / 1000);
    setText(bar.left, `${m}:${String(s).padStart(2, '0')} left`);
  }

  // ------------------------------------------------------------------ the orb
  function orb(docked) {
    const el = h('div.orb' + (docked ? '.docked' : ''), { 'data-state': 'idle', role: 'button', tabindex: docked ? '-1' : '0', title: 'Talk', 'aria-label': 'Talk to Pocket Agent' },
      h('span.glow'), h('span.ripple'), h('span.ripple.r2'), h('span.core'), h('span.shine'), h('span.ring'));
    el.addEventListener('click', () => send('voice.toggle'));
    el.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); send('voice.toggle'); } });
    return el;
  }

  function orbState(st) {
    if (!st) return 'off';
    if ((st.prompts || []).length) return 'prompt';
    const v = st.voice.state;
    if (v === 'listening') return 'listening';
    if (v === 'transcribing') return 'transcribing';
    if (st.agent.paused && st.agent.running) return 'paused';
    if (v === 'speaking') return 'speaking';
    if (st.agent.running) return st.agent.state === 'acting' ? 'acting' : 'thinking';
    return st.agent.notReady ? 'off' : 'idle';
  }

  // ------------------------------------------------------------------ assistant page
  const composer = {};
  const assistant = {};

  function buildAssistant() {
    const page = $('#page-assistant');
    assistant.bigOrb = orb(false);
    assistant.heroTitle = h('h2');
    assistant.heroText = h('p', { text: 'Pocket Agent works in your apps while you watch. It asks before using a new app and before anything that sends, deletes or pays.' });
    const examples = [
      'Write a packing list in Notepad',
      'Turn on dark mode',
      'Open the newest PDF in Downloads',
      'Summarise the page I have open',
    ];
    assistant.hero = h('div.hero', assistant.bigOrb, assistant.heroTitle, assistant.heroText,
      h('div.examples', examples.map(text => h('button.chip', { type: 'button', text, onclick: () => { composer.input.value = text; autosize(); composer.input.focus(); } }))));
    assistant.messages = h('div.messages', { 'aria-live': 'polite' });
    assistant.conversation = h('div.conversation', assistant.hero, assistant.messages);

    assistant.notReady = h('div.banner.info', { hidden: true });
    assistant.prompts = h('div', { hidden: true });
    assistant.paused = h('div.banner', { hidden: true });

    composer.orb = orb(true);
    composer.input = h('textarea', { rows: 1, placeholder: 'Ask Pocket Agent to do something…', 'aria-label': 'Message' });
    composer.mic = h('button.btn.icon-only.mic', { type: 'button', title: 'Talk (or hold your talk key)', 'aria-label': 'Talk', 'aria-pressed': 'false', onclick: () => send('voice.toggle') }, icon('mic'));
    composer.send = h('button.btn.primary.icon-only', { type: 'button', title: 'Send', 'aria-label': 'Send', onclick: submit }, icon('send'));
    composer.stop = h('button.btn.stop.icon-only', { type: 'button', title: 'Stop the task', 'aria-label': 'Stop the task', hidden: true, onclick: () => send('agent.stop') }, icon('stop'));
    composer.box = h('div.composer', composer.orb, composer.input, composer.mic, composer.send, composer.stop);
    composer.hint = h('div.hint');
    composer.input.addEventListener('input', () => { autosize(); renderComposerButtons(); });
    composer.input.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); submit(); }
    });

    const dock = h('div.dock', assistant.notReady, assistant.prompts, assistant.paused, composer.box, composer.hint);
    const talk = h('div.talk', assistant.conversation, dock);

    assistant.stepsState = h('span.state');
    assistant.pauseBtn = h('button.btn.small.ghost.icon-only', { type: 'button', title: 'Pause', 'aria-label': 'Pause', hidden: true, onclick: () => send(app.state && app.state.agent.paused ? 'agent.resume' : 'agent.pause') }, icon('pause', 'sm'));
    assistant.newBtn = h('button.btn.small.ghost.icon-only', { type: 'button', title: 'Start a new conversation', 'aria-label': 'New conversation', onclick: () => send('chat.new') }, icon('compose', 'sm'));
    assistant.steps = h('div.steps');
    assistant.usage = h('div.usage');
    const panel = h('aside.steps-panel', { 'aria-label': 'Steps' },
      h('div.steps-head', h('h2', { text: 'Steps' }), assistant.stepsState, assistant.pauseBtn, assistant.newBtn),
      assistant.steps, assistant.usage);
    put(page, h('div.assistant', talk, panel));
  }

  function autosize() {
    const t = composer.input;
    t.style.height = 'auto';
    t.style.height = Math.min(160, t.scrollHeight) + 'px';
  }

  function submit() {
    const text = composer.input.value.trim();
    if (!text) return;
    send('chat.send', { text });
    composer.input.value = '';
    autosize();
    renderComposerButtons();
  }

  function renderComposerButtons() {
    const st = app.state;
    const running = !!(st && st.agent.running);
    const typed = composer.input.value.trim().length > 0;
    show(composer.send, !running || typed);
    show(composer.stop, running && !typed);
    composer.send.title = running ? 'Add to the running task' : 'Send';
  }

  function talkPhrase(st) {
    const key = st.voice.talkKey;
    return st.voice.talkMode === 'toggle' ? `Press ${key}` : `Hold ${key}`;
  }

  function renderAssistant(st) {
    const state = orbState(st);
    assistant.bigOrb.dataset.state = state;
    composer.orb.dataset.state = state;
    setText(assistant.heroTitle, `${talkPhrase(st)} and say what you need.`);
    show(assistant.hero, app.chat.length === 0);

    // Not ready yet
    show(assistant.notReady, !!st.agent.notReady);
    if (st.agent.notReady && assistant.notReady.dataset.reason !== st.agent.notReady) {
      assistant.notReady.dataset.reason = st.agent.notReady;
      put(clear(assistant.notReady), 
        icon('key'),
        h('div.text', h('b', { text: 'Set up the AI model' }), h('span', { text: st.agent.notReady })),
        h('button.btn.small.primary', { type: 'button', text: 'Open AI settings', onclick: () => { go('settings'); scrollToSection('ai'); } }));
    }

    renderPrompts(st);

    // Paused
    const paused = st.agent.paused && st.agent.running;
    show(assistant.paused, paused);
    if (paused) {
      const reason = st.agent.detail || 'Paused.';
      if (assistant.paused.dataset.reason !== reason) {
        assistant.paused.dataset.reason = reason;
        put(clear(assistant.paused), 
          icon('hand'),
          h('div.text', h('b', { text: 'Paused' }), h('span', { text: reason })),
          h('button.btn.small.primary', { type: 'button', onclick: () => send('agent.resume') }, icon('play', 'sm'), 'Resume'),
          h('button.btn.small.stop', { type: 'button', text: 'Stop task', onclick: () => send('agent.stop') }));
      }
    }

    composer.mic.setAttribute('aria-pressed', String(st.voice.state === 'listening'));
    renderComposerButtons();
    const hintKey = talkPhrase(st) + st.voice.talkKey;
    if (composer.hint.dataset.key !== hintKey) {
      composer.hint.dataset.key = hintKey;
      put(clear(composer.hint), 
      st.voice.talkMode === 'toggle' ? 'Press ' : 'Hold ', h('kbd', { text: st.voice.talkKey }), ' to talk from any app. ',
      h('kbd', { text: 'Enter' }), ' sends. ',
      h('kbd', { text: 'Esc' }), ' three times stops everything.');
    }

    // Steps header and usage
    const a = st.agent;
    setText(assistant.stepsState, a.running ? (a.paused ? 'Paused' : a.state === 'acting' ? 'Working' : 'Thinking') : '');
    show(assistant.pauseBtn, a.running);
    if (assistant.pauseBtn.dataset.paused !== String(!!a.paused)) {
      assistant.pauseBtn.dataset.paused = String(!!a.paused);
      put(clear(assistant.pauseBtn), icon(a.paused ? 'play' : 'pause', 'sm'));
    }
    assistant.pauseBtn.title = a.paused ? 'Resume' : 'Pause';
    const u = a.usage;
    const usageKey = [u.input, u.output, u.cacheRead, a.cost, a.provider, a.model].join('|');
    if (assistant.usage.dataset.key === usageKey) return;
    assistant.usage.dataset.key = usageKey;
    clear(assistant.usage);
    if (u.input + u.output + u.cacheRead > 0) {
      put(assistant.usage, 
        h('span', 'Input ', h('b', { text: fmtTokens(u.input) })),
        u.cacheRead ? h('span', 'Cached ', h('b', { text: fmtTokens(u.cacheRead) })) : null,
        h('span', 'Output ', h('b', { text: fmtTokens(u.output) })),
        a.cost != null ? h('span', 'Cost ', h('b', { text: '≈ $' + a.cost.toFixed(a.cost < 1 ? 3 : 2) })) : h('span', { text: 'Add prices in Settings to see cost' }));
    } else {
      put(assistant.usage, h('span', { text: `${a.provider}, ${a.model || 'no model chosen'}` }));
    }
  }

  function renderPrompts(st) {
    const prompts = st.prompts || [];
    show(assistant.prompts, prompts.length > 0);
    const key = prompts.map(p => p.id).join(',');
    if (assistant.prompts.dataset.key === key) { updatePromptTimers(); return; }
    assistant.prompts.dataset.key = key;
    clear(assistant.prompts);
    for (const p of prompts.slice(0, 2)) {
      const timer = h('span.timer', { 'data-expires': p.expiresAt });
      put(assistant.prompts, h('div.prompt-card' + (p.kind === 'risk' ? '.risk' : ''), { role: 'alertdialog', 'aria-label': p.title },
        h('div.top', h('div.badge', icon(p.kind === 'risk' ? 'warn' : 'lock')),
          h('div', h('h3', { text: p.title }), h('div.detail', { text: p.detail }))),
        h('div.choices', p.choices.map(c => h('button.btn.small' + (c.style === 'primary' ? '.primary' : c.style === 'danger' ? '.stop' : ''),
          { type: 'button', text: c.label, onclick: () => send('prompt.answer', { id: p.id, choice: c.id }) })), timer)));
    }
    updatePromptTimers();
  }

  function updatePromptTimers() {
    for (const t of document.querySelectorAll('.prompt-card .timer')) {
      const left = Math.max(0, Math.round((Number(t.dataset.expires) - Date.now()) / 1000));
      setText(t, `${left} s`);
    }
  }

  // ---- conversation (keyed)
  const messageNodes = new Map();

  function messageNode(m) {
    if (m.role === 'user') {
      return h('div.msg.user', h('div.body', { text: m.text }),
        h('div.meta', m.source === 'voice' ? h('span.via', icon('mic', 'sm'), `Spoken at ${fmtTime(m.time)}`) : fmtTime(m.time)));
    }
    if (m.role === 'assistant') {
      return h('div.msg.assistant' + (m.source === 'narration' ? '.narration' : ''), h('span.avatar'), h('div.body', { text: m.text }));
    }
    const problem = /error|failed|couldn|can't|rejected|missing|limit/i.test(m.text);
    return h('div.msg.system' + (problem ? '.problem' : ''), h('div.body', { text: m.text }));
  }

  function renderMessages() {
    const box = assistant.messages;
    const nearBottom = assistant.conversation.scrollHeight - assistant.conversation.scrollTop - assistant.conversation.clientHeight < 120;
    const ids = new Set(app.chat.map(m => m.id));
    for (const [id, node] of messageNodes) if (!ids.has(id)) { node.remove(); messageNodes.delete(id); }
    for (const m of app.chat) {
      if (!messageNodes.has(m.id)) {
        const node = messageNode(m);
        messageNodes.set(m.id, node);
        put(box, node);
      }
    }
    show(assistant.hero, app.chat.length === 0);
    if (nearBottom) assistant.conversation.scrollTop = assistant.conversation.scrollHeight;
  }

  // ---- steps (keyed, grouped by run)
  const stepNodes = new Map();

  function stepNode(s) {
    const node = h('div.step', { 'data-status': s.status },
      h('span.num'), h('span.title'), h('span.ms'), h('div.detail'));
    node._img = null;
    updateStepNode(node, s);
    return node;
  }

  function updateStepNode(node, s) {
    node.dataset.status = s.status;
    setText(node.children[0], String(s.index || '·'));
    setText(node.children[1], s.title);
    setText(node.children[2], s.status === 'running' ? '' : fmtMs(s.ms || 0));
    const detail = s.status === 'running' ? '' : (s.detail || '');
    setText(node.children[3], detail);
    node.children[3].hidden = !detail;
    if (s.thumb && !node._img) {
      node._img = h('img.thumb', { src: s.thumb, alt: 'Screenshot', loading: 'lazy', onclick: () => lightbox(s.thumb) });
      put(node, node._img);
    }
    node.title = { done: 'Done', error: 'Failed', denied: 'Not allowed', interrupted: 'Interrupted', running: 'Running' }[s.status] || '';
  }

  function renderSteps() {
    const list = [...app.steps.values()].sort((a, b) => a.time - b.time).slice(-160);
    if (list.length === 0) {
      put(clear(assistant.steps), h('div.steps-empty', { text: 'Steps appear here while the assistant works, with a picture of each screenshot it takes.' }));
      stepNodes.clear();
      return;
    }
    const empty = assistant.steps.querySelector('.steps-empty');
    if (empty) empty.remove();
    const nearBottom = assistant.steps.scrollHeight - assistant.steps.scrollTop - assistant.steps.clientHeight < 100;
    const keep = new Set(list.map(s => s.id));
    for (const [id, node] of stepNodes) if (!keep.has(id)) { node.remove(); stepNodes.delete(id); }
    for (const label of assistant.steps.querySelectorAll('.run-label')) label.remove();
    let lastRun = null;
    let mcpCount = 0;
    const fragment = [];
    for (const s of list) {
      if (s.runId !== lastRun) {
        lastRun = s.runId;
        fragment.push(h('div.run-label', { text: s.source === 'mcp' ? 'Another assistant, through the connection' : `Task started ${fmtTime(s.time)}` }));
      }
      if (s.source === 'mcp') s.index = ++mcpCount;
      let node = stepNodes.get(s.id);
      if (!node) { node = stepNode(s); stepNodes.set(s.id, node); }
      else updateStepNode(node, s);
      fragment.push(node);
    }
    append(assistant.steps, fragment);
    if (nearBottom) assistant.steps.scrollTop = assistant.steps.scrollHeight;
  }

  // ------------------------------------------------------------------ activity
  const activity = {};
  const LOG_FILTERS = [['all', 'Everything'], ['action', 'Actions'], ['approval', 'Approvals'], ['voice', 'Voice'], ['error', 'Problems'], ['system', 'System']];

  function buildActivity() {
    const page = $('#page-activity');
    activity.filters = h('div.segmented', { role: 'group', 'aria-label': 'Filter' },
      LOG_FILTERS.map(([id, label]) => h('button', { type: 'button', text: label, 'aria-pressed': String(id === app.logFilter), onclick: e => {
        app.logFilter = id;
        for (const b of activity.filters.children) b.setAttribute('aria-pressed', String(b === e.currentTarget));
        renderLogs();
      } })));
    activity.search = h('input.input', { type: 'search', placeholder: 'Search activity', 'aria-label': 'Search activity', oninput: debounce(() => { app.logSearch = activity.search.value.toLowerCase(); renderLogs(); }, 150) });
    activity.list = h('div.log');
    put(page, h('div.page-inner',
      h('p.lead', { text: 'Everything the assistant and connected apps did on this PC, newest first. Typed text is never recorded.' }),
      h('div.toolbar', activity.filters, activity.search, h('div', { style: { flex: '1' } }),
        h('button.btn.small', { type: 'button', onclick: () => send('log.export') }, icon('download', 'sm'), 'Save as file'),
        h('button.btn.small.ghost', { type: 'button', text: 'Clear', onclick: () => send('log.clear') })),
      activity.list));
  }

  function renderLogs() {
    if (app.page !== 'activity') return;
    const filter = app.logFilter, q = app.logSearch;
    const rows = app.logs.filter(l => (filter === 'all' || l.category === filter || (filter === 'action' && l.category === 'mcp')) && (!q || l.message.toLowerCase().includes(q)))
      .slice(-400).reverse();
    clear(activity.list);
    if (rows.length === 0) { put(activity.list, h('div.log-empty', { text: app.logs.length ? 'Nothing matches this filter.' : 'Nothing has happened yet.' })); return; }
    const names = { action: 'Action', approval: 'Approval', voice: 'Voice', error: 'Problem', system: 'System', mcp: 'Connection', agent: 'Assistant' };
    for (const l of rows) {
      put(activity.list, h('div.log-row', h('time', { text: fmtClock(l.time) }), h('span.tag.' + l.category, { text: names[l.category] || l.category }), h('span', { text: l.message })));
    }
  }

  // ------------------------------------------------------------------ connections
  const conn = {};

  function buildConnections() {
    const page = $('#page-connections');
    conn.addressCode = h('code');
    conn.address = h('div.address', conn.addressCode,
      h('button.btn.small.ghost.icon-only', { type: 'button', title: 'Show the full address for 20 seconds', 'aria-label': 'Show address', onclick: () => send('secret.reveal') }, icon('eye', 'sm')),
      h('button.btn.small', { type: 'button', onclick: () => send('secret.copy', { which: 'local' }) }, icon('copy', 'sm'), 'Copy'));
    conn.addressNote = h('div.note');
    conn.mcpError = h('div.note.bad', { hidden: true });
    conn.clients = h('div.row', h('span.help', { text: 'Copy the setup for' }),
      h('button.btn.small', { type: 'button', text: 'Cursor', onclick: () => send('secret.copy', { which: 'cursor' }) }),
      h('button.btn.small', { type: 'button', text: 'VS Code', onclick: () => send('secret.copy', { which: 'vscode' }) }),
      h('button.btn.small', { type: 'button', text: 'Claude Desktop', onclick: () => send('secret.copy', { which: 'claude-desktop' }) }));

    const local = h('div.panel',
      h('header', h('div', h('h2', { text: 'Address for apps on this PC' }),
        h('p', { text: 'Assistants that speak MCP (Claude Desktop, Cursor, VS Code and others) can use the same Windows tools as the built-in assistant. They also need your approval for each app.' }))),
      h('div.stack', conn.mcpError, conn.address, conn.addressNote, conn.clients,
        h('div', bindSwitch('mcpServerEnabled', 'Let other assistants connect', 'Runs a private server on this PC while control is on.'),
          bindSwitch('reuseSecret', 'Keep the same address after restarts', 'Convenient for saved client settings. Anyone who learns it can control this PC while control is on.')),
        h('div.row', h('div.field', { style: { width: '160px' } }, h('label', { text: 'Port' }), bindInput('port', { type: 'number', min: 1024, max: 65535 })),
          h('div.help', { style: { flex: '1', alignSelf: 'end', paddingBottom: '8px' }, text: 'A new port takes effect the next time control starts.' }),
          h('button.btn.small', { type: 'button', style: { alignSelf: 'end', marginBottom: '4px' }, onclick: () => send('secret.rotate') }, icon('refresh', 'sm'), 'Make a new address'))));

    conn.tunnelCards = h('div.choice-cards', [
      ['off', 'Off', 'Only apps on this PC can connect.'],
      ['cloudflare', 'Cloudflare quick tunnel', 'A free, temporary https address. No account needed.'],
      ['custom', 'My own tunnel', 'Tailscale Funnel, ngrok or similar, already running.'],
    ].map(([id, title, text]) => h('button.choice-card', { type: 'button', 'data-id': id, onclick: () => send('settings.update', { patch: { tunnelMode: id } }) }, h('b', { text: title }), h('span', { text }))));
    conn.tunnelBody = h('div.stack');
    const remote = h('div.panel',
      h('header', h('div', h('h2', { text: 'Reach this PC from the internet' }),
        h('p', { text: 'Tunnel mode lets an assistant in the cloud (for example a custom connector) reach this PC. Leave it off unless you need it.' }))),
      h('div.stack', conn.tunnelCards, conn.tunnelBody,
        h('div.note.warn', { text: 'Anyone who has the public address can control this PC while control is on. Share it only with your own assistant, and stop control when you are done.' })));
    put(page, h('div.page-inner', local, remote));
  }

  function renderConnections(st) {
    const c = st.control, t = st.tunnel, s = st.settings;
    const revealed = app.revealed && app.revealed.until > Date.now() ? app.revealed : null;
    conn.address.classList.toggle('off', !c.active);
    setText(conn.addressCode, !c.active ? 'Starts when desktop control is on' : !c.mcpRunning ? 'Not running' : (revealed && revealed.local) || c.localUrlMasked || '');
    show(conn.clients, c.active && c.mcpRunning);
    show(conn.mcpError, !!c.mcpError);
    setText(conn.mcpError, c.mcpError || '');
    setText(conn.addressNote, !c.active
      ? 'Start desktop control to create the address. Unless you keep it, it changes every time.'
      : s.reuseSecret ? 'This address stays the same until you make a new one.' : 'This address works until control stops.');

    for (const card of conn.tunnelCards.children) card.setAttribute('aria-pressed', String(card.dataset.id === t.mode));
    const key = [t.mode, t.status, t.error, t.cloudflaredFound, c.active, c.publicUrlMasked, Math.round(t.progress * 20), revealed ? 1 : 0, s.customTunnelUrl].join('|');
    if (conn.tunnelBody.dataset.key === key) return;
    conn.tunnelBody.dataset.key = key;
    clear(conn.tunnelBody);
    if (t.mode === 'off') return;
    if (t.mode === 'cloudflare') {
      if (!t.cloudflaredFound && t.status !== 'downloading') {
        put(conn.tunnelBody, h('div.row', h('div.grow.help', { text: 'Pocket Agent uses Cloudflare’s free cloudflared program for this. It isn’t installed yet.' }),
          h('button.btn.small.primary', { type: 'button', onclick: () => send('tunnel.download') }, icon('download', 'sm'), 'Download cloudflared')));
      }
      if (t.status === 'downloading') put(conn.tunnelBody, h('div.help', { text: 'Downloading cloudflared from GitHub…' }), h('div.progress', h('i', { style: { width: Math.round(t.progress * 100) + '%' } })));
      if (t.cloudflaredFound) {
        const statusText = { off: 'Tunnel is off.', starting: 'Starting the tunnel…', online: 'Tunnel is online.', error: t.error || 'The tunnel stopped.' }[t.status] || '';
        put(conn.tunnelBody, h('div.row', h('div.grow', h('b', { text: statusText }), !c.active ? h('div.help', { text: 'It starts automatically with desktop control.' }) : null),
          c.active && t.status !== 'online' && t.status !== 'starting' ? h('button.btn.small.primary', { type: 'button', text: 'Start tunnel', onclick: () => send('tunnel.start') }) : null,
          t.status === 'online' || t.status === 'starting' ? h('button.btn.small', { type: 'button', text: 'Stop tunnel', onclick: () => send('tunnel.stop') }) : null));
      }
    } else if (t.mode === 'custom') {
      put(conn.tunnelBody, h('div.field', h('label', { text: 'Your tunnel’s https address' }), bindInput('customTunnelUrl', { placeholder: 'https://my-pc.example.ts.net' }),
        h('div.help', { text: `Point your tunnel at http://127.0.0.1:${s.port}. Pocket Agent adds the secret path.` })));
    }
    if (c.publicUrlMasked) {
      put(conn.tunnelBody, h('div.address', h('code', { text: (revealed && revealed.public) || c.publicUrlMasked }),
        h('button.btn.small', { type: 'button', onclick: () => send('secret.copy', { which: 'public' }) }, icon('copy', 'sm'), 'Copy public address')));
    }
  }

  // ------------------------------------------------------------------ approvals
  const appr = {};

  function buildApprovals() {
    const page = $('#page-approvals');
    const pocket = (kind, title, text, iconName) => {
      const list = h('div');
      const el = h('div.pocket.' + kind, h('h3', icon(iconName, 'sm'), title), h('p', { text }), list);
      return { el, list };
    };
    appr.session = pocket('session', 'This session', 'Allowed until desktop control stops.', 'clock');
    appr.always = pocket('always', 'Always', 'Allowed every time, until you remove them.', 'check');
    appr.never = pocket('never', 'Never', 'The assistant may not read or use these, even if asked.', 'x');
    appr.blockInput = h('input.input', { placeholder: 'e.g. banking.exe', 'aria-label': 'App to block' });
    appr.never.el.insertBefore(h('form', { onsubmit: e => { e.preventDefault(); const v = appr.blockInput.value.trim(); if (v) { send('approvals.block', { name: v }); appr.blockInput.value = ''; } } },
      appr.blockInput, h('button.btn.small', { type: 'submit', text: 'Block' })), appr.never.list);
    put(page, h('div.page-inner',
      h('p.lead', { text: 'The assistant asks the first time it needs an app. Your answers are kept here. Password managers start on the never list.' }),
      h('div.pockets', appr.session.el, appr.always.el, appr.never.el)));
  }

  function renderApprovals(st) {
    const a = st.approvals;
    const fill = (pocket, entries, action, emptyText) => {
      const key = JSON.stringify(entries.map(e => e.key));
      if (pocket.list.dataset.key === key) return;
      pocket.list.dataset.key = key;
      clear(pocket.list);
      if (entries.length === 0) { put(pocket.list, h('div.empty', { text: emptyText })); return; }
      for (const e of entries) {
        put(pocket.list, h('div.entry', h('b', { text: e.label, title: e.label }), h('small', { text: e.detail, title: e.detail }),
          h('button.btn.small.ghost.icon-only', { type: 'button', title: 'Remove', 'aria-label': 'Remove ' + e.label, onclick: () => send(action, { key: e.key }) }, icon('trash', 'sm'))));
      }
    };
    fill(appr.session, a.session, 'approvals.revoke', 'Nothing yet this session.');
    fill(appr.always, a.always, 'approvals.revoke', 'No apps are always allowed.');
    fill(appr.never, a.blocked, 'approvals.unblock', 'No blocked apps.');
  }

  // ------------------------------------------------------------------ settings bindings
  function settingsValue(path) { return app.state ? get(app.state.settings, path) : undefined; }
  function update(path, value) { send('settings.update', { patch: patchFor(path, value) }); }

  function bindSwitch(path, title, text, opts = {}) {
    const toggle = h('button.toggle', { type: 'button', role: 'switch', 'aria-label': title, onclick: () => update(path, !settingsValue(path)) });
    bind(toggle, () => {
      toggle.setAttribute('aria-checked', String(!!settingsValue(path)));
      toggle.disabled = opts.disabled ? !!opts.disabled() : false;
    });
    return h('div.switch', h('div.text', h('b', { text: title }), text ? h('span', { text }) : null), toggle);
  }

  function bindInput(path, opts = {}) {
    const input = h('input.input', { type: opts.type || 'text', placeholder: opts.placeholder || '', min: opts.min, max: opts.max, step: opts.step, list: opts.list, 'aria-label': opts.label || path, spellcheck: 'false' });
    const commit = () => {
      let v = input.value;
      if (opts.type === 'number') { if (v === '') v = opts.emptyValue === undefined ? null : opts.emptyValue; else v = Number(v); }
      if (v !== settingsValue(path)) update(path, v);
    };
    input.addEventListener('change', commit);
    input.addEventListener('keydown', e => { if (e.key === 'Enter') { commit(); input.blur(); } });
    bind(input, () => {
      if (document.activeElement === input) return;
      const v = settingsValue(path);
      const text = v === null || v === undefined ? '' : String(v);
      if (input.value !== text) input.value = text;
      if (opts.placeholderFrom) input.placeholder = opts.placeholderFrom() || '';
    });
    return input;
  }

  function bindTextarea(path, opts = {}) {
    const area = h('textarea.textarea', { rows: opts.rows || 3, placeholder: opts.placeholder || '', 'aria-label': opts.label || path });
    area.addEventListener('change', () => update(path, area.value));
    bind(area, () => {
      if (document.activeElement === area) return;
      const v = settingsValue(path) || '';
      if (area.value !== v) area.value = v;
    });
    return area;
  }

  function bindSelect(path, options, opts = {}) {
    const select = h('select.select', { 'aria-label': opts.label || path });
    let lastKey = '';
    const fill = list => {
      const key = JSON.stringify(list);
      if (key === lastKey) return;
      lastKey = key;
      put(clear(select), list.map(([value, label]) => h('option', { value: String(value), text: label })));
    };
    if (typeof options !== 'function') fill(options);
    select.addEventListener('change', () => update(path, opts.number ? Number(select.value) : select.value));
    bind(select, () => {
      if (typeof options === 'function') fill(options());
      if (document.activeElement === select) return;
      const v = String(settingsValue(path) ?? '');
      if (select.value !== v) select.value = v;
    });
    return select;
  }

  function bindSegmented(path, options, opts = {}) {
    const group = h('div.segmented', { role: 'group', 'aria-label': opts.label || path },
      options.map(([value, label]) => h('button', { type: 'button', text: label, 'data-value': String(value), onclick: () => {
        update(path, value);
        if (opts.onPick) opts.onPick(value);
      } })));
    bind(group, () => {
      const v = String(settingsValue(path));
      for (const b of group.children) b.setAttribute('aria-pressed', String(b.dataset.value === v));
    });
    return group;
  }

  function bindRange(path, min, max, step, format) {
    const out = h('span.help');
    const range = h('input', { type: 'range', min, max, step, 'aria-label': path });
    range.addEventListener('input', () => setText(out, format(Number(range.value))));
    range.addEventListener('change', () => update(path, Number(range.value)));
    bind(range, () => {
      if (document.activeElement === range) return;
      const v = Number(settingsValue(path));
      if (Number(range.value) !== v) range.value = String(v);
      setText(out, format(v));
    });
    return h('div.row', h('div.grow', range), out);
  }

  function keyField(target, keySet, keyHint, placeholder) {
    const input = h('input.input', { type: 'password', placeholder, autocomplete: 'off', spellcheck: 'false', 'aria-label': 'API key' });
    const state = h('span.key-state');
    const save = () => {
      const v = input.value.trim();
      if (!v) return;
      send('settings.setKey', { target, key: v });
      input.value = '';
    };
    input.addEventListener('keydown', e => { if (e.key === 'Enter') save(); });
    bind(state, () => {
      const set = keySet();
      state.className = 'key-state' + (set ? '' : ' missing');
      put(clear(state), icon(set ? 'check' : 'key', 'sm'), set ? `Saved ${keyHint()}` : 'No key saved');
    });
    const remove = h('button.btn.small.ghost', { type: 'button', text: 'Remove', onclick: () => send('settings.setKey', { target, key: '' }) });
    bind(remove, () => show(remove, keySet()));
    return h('div.stack', { style: { gap: '8px' } }, h('div.row', h('div.grow', input), h('button.btn', { type: 'button', text: 'Save key', onclick: save })), h('div.row', state, remove,
      h('span.help', { text: 'Stored encrypted for your Windows account.' })));
  }

  // ------------------------------------------------------------------ settings page
  const settings = {};
  const SECTIONS = [['ai', 'AI model'], ['voice', 'Voice'], ['safety', 'Safety'], ['appearance', 'Appearance'], ['about', 'About']];
  const COMMON_LANGUAGES = ['en-US', 'en-GB', 'de-DE', 'fr-FR', 'es-ES', 'it-IT', 'nl-NL', 'pt-BR', 'hi-IN', 'ta-IN', 'ja-JP', 'zh-CN'];
  const OPENAI_VOICES = ['alloy', 'ash', 'ballad', 'coral', 'echo', 'fable', 'nova', 'onyx', 'sage', 'shimmer', 'verse'];

  function scrollToSection(id) {
    const el = $('#set-' + id);
    if (el) el.scrollIntoView({ block: 'start' });
    for (const b of settings.subnav.children) b.setAttribute('aria-current', String(b.dataset.id === id));
  }

  function buildSettings() {
    const page = $('#page-settings');
    settings.subnav = h('nav.subnav', { 'aria-label': 'Settings sections' },
      SECTIONS.map(([id, label]) => h('button', { type: 'button', text: label, 'data-id': id, onclick: () => scrollToSection(id) })));

    // AI model
    settings.providers = h('div.providers');
    settings.providerDetail = h('div.stack');
    const ai = h('section#set-ai',
      h('h2', { text: 'AI model' }),
      h('p.lead', { text: 'Pocket Agent works with any provider. Your key stays on this PC and is only sent to the provider you choose.' }),
      h('div.panel', h('div.stack', settings.providers, settings.providerDetail)),
      h('div.panel', h('div.stack',
        h('div.grid-2',
          h('div.field', h('label', { text: 'Step limit' }), bindSelect('maxSteps', [[0, 'No limit — keep going until done'], [25, '25 steps'], [50, '50 steps'], [100, '100 steps'], [250, '250 steps']], { number: true }),
            h('div.help', { text: 'With no limit, it works until the task is done or you stop it.' })),
          h('div.field', h('label', { text: 'Screenshots it remembers' }), bindSelect('keepScreenshots', [[1, 'Latest 1'], [2, 'Latest 2'], [3, 'Latest 3 (recommended)'], [5, 'Latest 5'], [8, 'Latest 8']], { number: true }),
            h('div.help', { text: 'Older ones are dropped to keep long tasks fast and cheap.' }))),
        h('div.field', h('label', { text: 'Your instructions' }), bindTextarea('customInstructions', { placeholder: 'For example: My browser is Edge. Reply in German. Save files to Documents\\Work.' }),
          h('div.help', { text: 'Added to every task. Safety rules still apply.' })))));

    // Voice
    settings.micMeter = h('i');
    settings.transcript = h('span.result', { text: '' });
    settings.whisperBox = h('div.stack');
    settings.speechBox = h('div.stack');
    const voice = h('section#set-voice',
      h('h2', { text: 'Voice' }),
      h('p.lead', { text: 'Talk to the assistant from any app. Your talk key works even when Pocket Agent is in the tray.' }),
      h('div.panel', h('div.stack',
        h('div.grid-2',
          h('div.field', h('label', { text: 'Talk key' }), bindSelect('talkKey', () => (app.state ? app.state.talkKeys.map(k => [k.id, k.label]) : []))),
          h('div.field', h('span.label', { text: 'How it works' }), bindSegmented('talkMode', [['hold', 'Hold to talk'], ['toggle', 'Tap on, tap off']]))),
        h('div', bindSwitch('wakeWordEnabled', 'Wake word: “Hey Pocket”', 'Start talking without touching the keyboard. Uses the microphone continuously while on.', { disabled: () => !(app.state && app.state.platform.wakeWordAvailable) }),
          bindSwitch('voiceCommandsEnabled', '“Pocket, stop” while it works', 'While a task runs, say “Pocket, stop”, “Pocket, pause” or “Pocket, continue”.', { disabled: () => !(app.state && app.state.platform.wakeWordAvailable) })),
        settings.wakeNote = h('div.note', { hidden: true }))),
      h('div.panel',
        h('header', h('div', h('h3', { text: 'Understanding you' }), h('p', { text: 'Windows speech is free and built in. A Whisper-compatible service (OpenAI, Groq or your own server) is usually more accurate.' }))),
        h('div.stack',
          bindSegmented('sttEngine', [['windows', 'Windows speech'], ['whisper', 'Whisper-compatible service']]),
          settings.whisperBox,
          h('div.field', h('label', { text: 'Language' }), bindSelect('language', () => languageOptions())),
          h('div.row',
            settings.micBtn = h('button.btn.small', { type: 'button', onclick: () => { app.micTestFromSettings = !app.micTestFromSettings; send('voice.micTest', { on: app.micTestFromSettings }); } }, icon('mic', 'sm'), 'Test microphone'),
            h('div.grow.meter', settings.micMeter),
            h('button.btn.small', { type: 'button', onclick: () => { settings.transcript.className = 'result'; setText(settings.transcript, 'Listening… speak now.'); send('voice.testListen'); } }, 'Try speech to text')),
          settings.transcript)),
      h('div.panel',
        h('header', h('div', h('h3', { text: 'Its voice' }), h('p', { text: 'Replies and short narration are read aloud. Talking or pressing the talk key interrupts it.' }))),
        h('div.stack',
          bindSegmented('ttsEngine', [['windows', 'Windows voices'], ['openai', 'OpenAI-compatible'], ['off', 'Silent']]),
          settings.speechBox,
          h('div', bindSwitch('speakReplies', 'Read replies aloud', 'Short final answers are spoken.'),
            bindSwitch('narrateSteps', 'Narrate what it is doing', 'A short sentence before a group of actions.')),
          h('div.row', h('button.btn.small', { type: 'button', onclick: () => send('voice.testSpeak') }, icon('volume', 'sm'), 'Play a sample'),
            h('button.btn.small.ghost', { type: 'button', text: 'Stop speaking', onclick: () => send('voice.stopSpeaking') })))));

    // Safety
    const safety = h('section#set-safety',
      h('h2', { text: 'Safety' }),
      h('p.lead', { text: 'You stay in charge. These rules are enforced by the app, not just suggested to the model.' }),
      h('div.panel', h('div.stack',
        h('div.grid-2',
          h('div.field', h('label', { text: 'Switch control off after' }), bindSelect('autoStopMinutes', [[10, '10 minutes'], [20, '20 minutes'], [30, '30 minutes'], [60, '1 hour'], [120, '2 hours'], [0, 'Never — until I stop it']], { number: true })),
          h('div.field', h('label', { text: 'Wait for my answer to a prompt' }), bindSelect('approvalTimeoutSeconds', [[30, '30 seconds'], [60, '1 minute'], [120, '2 minutes'], [300, '5 minutes']], { number: true }))),
        h('div',
          bindSwitch('confirmRiskySteps', 'Ask before risky clicks', 'Buttons that send, delete, pay, buy or submit need your OK first, and so does pressing Enter in chat apps.'),
          bindSwitch('pauseOnUserInput', 'Pause when I use the mouse or keyboard', 'Grab the mouse any time to take over. Press Resume or say “continue”.'),
          bindSwitch('tripleEscStop', 'Esc three times stops everything', 'Stops the task and switches desktop control off.'),
          bindSwitch('clickPreview', 'Show where it will click', 'A ring appears at the spot a moment before each click.'),
          bindSwitch('autoStartControlOnTalk', 'Turn control on when I ask for something', 'Otherwise you start control yourself first.')),
        h('div.field', h('label', { text: 'Click preview length' }), bindRange('clickPreviewMs', 150, 1000, 50, v => v + ' ms')),
        h('div.field', h('label', { text: 'Words that count as risky' }), bindTextarea('riskyPhrases', { rows: 4 }),
          h('div.row', h('div.grow.help', { text: 'Matched against the start or end of a button’s label, in any language you add.' }),
            h('button.btn.small.ghost', { type: 'button', text: 'Restore the default list', onclick: () => app.state && update('riskyPhrases', app.state.defaultRiskyPhrases) }))))));

    // Appearance
    const appearance = h('section#set-appearance',
      h('h2', { text: 'Appearance' }),
      h('div.panel', h('div.stack',
        h('div.field', h('span.label', { text: 'Theme' }), bindSegmented('theme', [['system', 'Match Windows'], ['light', 'Light'], ['dark', 'Dark']])),
        h('div.field', h('span.label', { text: 'Voice bubble' }), bindSegmented('bubbleMode', [['always', 'Always on screen'], ['active', 'Only while busy'], ['never', 'Hidden']]),
          h('div.help', { text: 'The bubble floats over your apps, shows what it is doing and asks for approvals. It never takes focus and never appears in screenshots.' })),
        h('div', bindSwitch('startWithWindows', 'Start with Windows', 'Starts in the tray, ready for your talk key.'),
          bindSwitch('closeToTray', 'Keep running when the window is closed', 'Quit from the tray icon.')))));

    // About
    settings.version = h('b');
    const about = h('section#set-about',
      h('h2', { text: 'About' }),
      h('div.panel', h('div.stack',
        h('div.row', h('div.grow', h('div', 'Pocket Agent ', settings.version), h('div.help', { text: 'Settings, approvals and logs are stored in %LOCALAPPDATA%\\PocketAgentWindows.' }))),
        h('div.row',
          h('button.btn.small', { type: 'button', text: 'Run setup again', onclick: () => openSetup(0) }),
          h('button.btn.small.stop', { type: 'button', onclick: () => send('window.quit') }, icon('power', 'sm'), 'Quit Pocket Agent')))));

    const body = h('div', ai, voice, safety, appearance, about);
    put(page, h('div.page-inner', h('div.settings', settings.subnav, body)));
    page.addEventListener('scroll', debounce(() => {
      let current = 'ai';
      for (const [id] of SECTIONS) { const el = $('#set-' + id); if (el && el.getBoundingClientRect().top < 160) current = id; }
      for (const b of settings.subnav.children) b.setAttribute('aria-current', String(b.dataset.id === current));
    }, 60));
    bind(settings.subnav, () => {
      const st = app.state;
      const p = st.platform;
      const reasons = [p.wakeWordAvailable ? null : p.wakeWordError, !p.microphoneAvailable ? 'No microphone was found.' : null].filter(Boolean);
      show(settings.wakeNote, reasons.length > 0);
      setText(settings.wakeNote, reasons.join(' '));
      setText(settings.version, st.version);
      put(clear(settings.micBtn), icon('mic', 'sm'), app.micTestFromSettings ? 'Stop the test' : 'Test microphone');
      renderEngineBoxes(st);
    });
  }

  function languageOptions() {
    const langs = new Set(COMMON_LANGUAGES);
    if (app.state) for (const l of app.state.platform.recognizerLanguages || []) langs.add(l);
    return [['auto', 'Automatic'], ...[...langs].sort().map(l => [l, `${languageName(l)} (${l})`])];
  }

  function renderEngineBoxes(st) {
    const s = st.settings;
    const sttKey = s.sttEngine;
    if (settings.whisperBox.dataset.key !== sttKey) {
      settings.whisperBox.dataset.key = sttKey;
      clear(settings.whisperBox);
      if (sttKey === 'whisper') {
        put(settings.whisperBox, 
          h('div.grid-2',
            h('div.field', h('label', { text: 'Service address' }), bindInput('transcription.baseUrl', { placeholder: 'https://api.openai.com/v1' }),
              h('div.help', { text: 'Groq: https://api.groq.com/openai/v1' })),
            h('div.field', h('label', { text: 'Model' }), bindInput('transcription.model', { placeholder: 'gpt-transcribe' }),
              h('div.help', { text: 'Groq: whisper-large-v3-turbo' }))),
          bindSwitch('transcription.useProviderKey', 'Reuse my AI provider key', 'When the address matches a provider you set up.'),
          keyField('transcription', () => !!get(app.state, 'settings.transcription.keySet'), () => get(app.state, 'settings.transcription.keyHint') || '', 'Key for the speech service (optional)'));
        refreshBinders();
      }
    }
    const ttsKey = s.ttsEngine + '|' + (st.platform.voices || []).length;
    if (settings.speechBox.dataset.key !== ttsKey) {
      settings.speechBox.dataset.key = ttsKey;
      clear(settings.speechBox);
      if (s.ttsEngine === 'windows') {
        put(settings.speechBox, h('div.grid-2',
          h('div.field', h('label', { text: 'Voice' }), bindSelect('windowsVoice', () => [['', 'Automatic (matches the reply’s language)'], ...(app.state.platform.voices || []).map(v => [v.id, `${v.name} (${v.language})`])])),
          h('div.field', h('label', { text: 'Speed' }), bindRange('speechRate', 0.6, 1.8, 0.1, v => v.toFixed(1) + '×'))));
      } else if (s.ttsEngine === 'openai') {
        const voices = h('datalist#openai-voices', OPENAI_VOICES.map(v => h('option', { value: v })));
        put(settings.speechBox, voices,
          h('div.grid-2',
            h('div.field', h('label', { text: 'Service address' }), bindInput('speech.baseUrl', { placeholder: 'https://api.openai.com/v1' })),
            h('div.field', h('label', { text: 'Model' }), bindInput('speech.model', { placeholder: 'gpt-4o-mini-tts' })),
            h('div.field', h('label', { text: 'Voice' }), bindInput('speech.voice', { placeholder: 'alloy', list: 'openai-voices' })),
            h('div.field', h('label', { text: 'Speed' }), bindRange('speechRate', 0.6, 1.8, 0.1, v => v.toFixed(1) + '×'))),
          bindSwitch('speech.useProviderKey', 'Reuse my AI provider key', 'When the address matches a provider you set up.'),
          keyField('speech', () => !!get(app.state, 'settings.speech.keySet'), () => get(app.state, 'settings.speech.keyHint') || '', 'Key for the voice service (optional)'));
      }
      refreshBinders();
    }
  }

  function renderProviders(st) {
    const id = st.settings.providerId;
    const key = st.providers.map(p => p.id).join(',') + '|' + id;
    if (settings.providers.dataset.key !== key) {
      settings.providers.dataset.key = key;
      put(clear(settings.providers), st.providers.map(p => h('button.provider', { type: 'button', 'aria-pressed': String(p.id === id), onclick: () => update('providerId', p.id) },
        h('b', { text: p.name }), h('span', { text: p.requiresKey ? 'API key' : 'Runs on this PC' }))));
    }
    if (app.builtProviderId !== id) {
      app.builtProviderId = id;
      buildProviderDetail(settings.providerDetail, st, id, false);
      refreshBinders();
    }
    renderModelHelpers(id);
  }

  const providerUi = {};

  function buildProviderDetail(box, st, id, compact) {
    const p = st.providers.find(x => x.id === id) || st.providers[0];
    const base = `providers.${p.id}`;
    const list = 'models-' + p.id + (compact ? '-setup' : '');
    const datalist = h('datalist#' + list);
    const loadResult = h('span.result');
    const testResult = h('span.result');
    providerUi[p.id + (compact ? 'S' : '')] = { datalist, loadResult, testResult, suggested: p.suggestedModels || [] };
    put(clear(box), 
      h('div', h('h3', { text: p.name }), h('div.help', { text: p.notes })),
      p.requiresKey ? h('div.field', h('label', { text: 'API key' }),
        keyField('provider:' + p.id, () => !!get(app.state, `settings.${base}.keySet`), () => get(app.state, `settings.${base}.keyHint`) || '', 'Paste your API key'),
        p.keyUrl ? h('div', h('a', { href: '#', text: 'Get a key from ' + p.name, onclick: e => { e.preventDefault(); send('open.external', { url: p.keyUrl }); } })) : null) : null,
      h('div.field', h('label', { text: 'Model' }),
        h('div.row', h('div.grow', bindInput(base + '.model', { list, placeholderFrom: () => p.defaultModel || 'Choose a model' })),
          h('button.btn.small', { type: 'button', onclick: () => { setText(loadResult, 'Loading…'); loadResult.className = 'result'; send('models.load', { providerId: p.id }); } }, icon('refresh', 'sm'), 'Load models'),
          h('button.btn.small', { type: 'button', onclick: () => { setText(testResult, 'Testing…'); testResult.className = 'result'; send('provider.test', { providerId: p.id }); } }, 'Test')),
        datalist, h('div.row', loadResult, testResult)),
      compact ? null : h('details.more', h('summary', { text: 'Advanced' }), h('div.stack',
        h('div.field', h('label', { text: 'Server address' }), bindInput(base + '.baseUrl', { placeholderFrom: () => p.baseUrl || 'https://your-server/v1' })),
        p.allowStyleChoice ? h('div.field', h('label', { text: 'API format' }), bindSelect(base + '.apiStyle', [['', 'Default for this provider'], ['openai-responses', 'OpenAI Responses'], ['openai-chat', 'OpenAI Chat Completions'], ['anthropic', 'Anthropic Messages']])) : null,
        h('div.grid-2',
          h('div.field', h('label', { text: 'Input price per million tokens (US$)' }), bindInput(base + '.priceInput', { type: 'number', step: '0.01', min: 0, placeholder: 'Known models fill this in' })),
          h('div.field', h('label', { text: 'Output price per million tokens (US$)' }), bindInput(base + '.priceOutput', { type: 'number', step: '0.01', min: 0, placeholder: 'Optional' }))))));
  }

  function renderModelHelpers(id) {
    for (const suffix of ['', 'S']) {
      const ui = providerUi[id + suffix];
      if (!ui) continue;
      const loaded = app.models[id];
      const values = [...new Set([...(ui.suggested || []), ...((loaded && loaded.models) || [])])];
      const key = values.join(',');
      if (ui.datalist.dataset.key !== key) {
        ui.datalist.dataset.key = key;
        put(clear(ui.datalist), values.map(v => h('option', { value: v })));
      }
      if (loaded && loaded.fresh) {
        ui.loadResult.className = 'result ' + (loaded.error ? 'bad' : 'ok');
        setText(ui.loadResult, loaded.error ? loaded.error : `${loaded.models.length} models available. Pick one from the list.`);
      }
      const test = app.providerTests[id];
      if (test && test.fresh) {
        ui.testResult.className = 'result ' + (test.ok ? 'ok' : 'bad');
        setText(ui.testResult, test.message);
      }
    }
  }

  function refreshBinders() {
    if (!app.state) return;
    for (let i = binders.length - 1; i >= 0; i--) {
      const b = binders[i];
      if (b.el) {
        if (b.el.isConnected) b.seen = true;
        else if (b.seen) { binders.splice(i, 1); continue; }
      }
    }
    for (const b of binders) { try { b(); } catch (e) { console.error(e); } }
  }

  // ------------------------------------------------------------------ setup (first run)
  const setup = {};
  const SETUP_STEPS = 4;

  function openSetup(step) {
    app.setupOpen = true;
    app.setupStep = step;
    app.setupDismissed = false;
    renderSetup();
  }

  function closeSetup(complete) {
    app.setupOpen = false;
    app.setupDismissed = true;
    send('voice.micTest', { on: false });
    if (complete) send('setup.complete');
    show($('#setup'), false);
  }

  function renderSetup() {
    const root = $('#setup');
    const st = app.state;
    if (!st || !app.setupOpen) { show(root, false); return; }
    show(root, true);
    const step = app.setupStep;
    if (root.dataset.step === String(step) && root.dataset.provider === st.settings.providerId) { renderModelHelpers(st.settings.providerId); return; }
    root.dataset.step = String(step);
    root.dataset.provider = st.settings.providerId;
    send('voice.micTest', { on: step === 1 });
    const dots = h('div.progress-dots', Array.from({ length: SETUP_STEPS }, (_, i) => h('i' + (i <= step ? '.on' : ''))));
    const next = (label, to) => h('button.btn.primary', { type: 'button', text: label, onclick: () => { app.setupStep = to; renderSetup(); } });
    const back = step > 0 ? h('button.btn.ghost', { type: 'button', text: 'Back', onclick: () => { app.setupStep = step - 1; renderSetup(); } }) : null;
    let body;
    if (step === 0) {
      const providers = h('div.providers', st.providers.filter(p => ['anthropic', 'openai', 'gemini', 'openrouter', 'ollama', 'custom'].includes(p.id)).map(p =>
        h('button.provider', { type: 'button', 'aria-pressed': String(p.id === st.settings.providerId), onclick: () => update('providerId', p.id) },
          h('b', { text: p.name }), h('span', { text: p.requiresKey ? 'API key' : 'Runs on this PC' }))));
      const detail = h('div.stack');
      buildProviderDetail(detail, st, st.settings.providerId, true);
      body = [h('h2', { text: 'Choose the AI that does the work' }),
        h('p.lead', { text: 'Pocket Agent works with any provider. Paste a key, check it with Test, and you’re set. You can change this any time.' }),
        h('div.stack', providers, detail),
        h('footer', h('button.btn.ghost', { type: 'button', text: 'Skip — I’ll only connect other assistants', onclick: () => { app.setupStep = 1; renderSetup(); } }), h('div.spacer'), next('Next', 1))];
    } else if (step === 1) {
      setup.meter = h('i');
      setup.transcript = h('span.result');
      body = [h('h2', { text: 'Talk to it from any app' }),
        h('p.lead', { text: 'Pick a key you can hold while you speak. When you let go, it gets to work. Talking again interrupts it.' }),
        h('div.stack',
          h('div.grid-2',
            h('div.field', h('label', { text: 'Talk key' }), bindSelect('talkKey', () => st.talkKeys.map(k => [k.id, k.label]))),
            h('div.field', h('span.label', { text: 'Its voice' }), bindSegmented('ttsEngine', [['windows', 'Windows voice'], ['off', 'Silent']]))),
          h('div.field', h('span.label', { text: 'Microphone' }), h('div.meter', setup.meter), h('div.help', { text: 'The bar moves when it hears you.' })),
          h('div.row',
            h('button.btn.small', { type: 'button', onclick: () => { setup.transcript.className = 'result'; setText(setup.transcript, 'Listening… say a sentence.'); send('voice.testListen'); } }, icon('mic', 'sm'), 'Try speech to text'),
            h('button.btn.small', { type: 'button', onclick: () => send('voice.testSpeak') }, icon('volume', 'sm'), 'Play its voice'), setup.transcript)),
        h('footer', back, h('div.spacer'), next('Next', 2))];
    } else if (step === 2) {
      body = [h('h2', { text: 'You stay in charge' }),
        h('p.lead', { text: 'The first time it needs an app, it asks you. It also asks before anything that sends, deletes or pays. Grab the mouse to take over, or press Esc three times to stop everything.' }),
        h('div.panel', h('div.stack',
          h('div.field', h('label', { text: 'Switch control off after' }), bindSelect('autoStopMinutes', [[10, '10 minutes'], [20, '20 minutes'], [30, '30 minutes'], [60, '1 hour'], [120, '2 hours'], [0, 'Never — until I stop it']], { number: true })),
          h('div', bindSwitch('confirmRiskySteps', 'Ask before risky clicks', 'Send, delete, pay, buy and submit need your OK.'),
            bindSwitch('pauseOnUserInput', 'Pause when I use the mouse or keyboard', 'Take over any time.')))),
        h('footer', back, h('div.spacer'), next('Next', 3))];
    } else {
      body = [h('h2', { text: `${talkPhrase(st)} and say what you need.` }),
        h('p.lead', { text: 'Pocket Agent lives in the tray and the floating bubble. Watch the steps as it works, and stop it whenever you like.' }),
        st.agent.notReady ? h('div.note.warn', { text: 'One thing left: ' + st.agent.notReady }) : h('div.note', { text: 'Try: “Open Notepad and write a packing list for a weekend trip.”' }),
        h('footer', back, h('div.spacer'), h('button.btn.primary', { type: 'button', text: 'Start using Pocket Agent', onclick: () => closeSetup(true) }))];
    }
    put(clear(root), h('div.sheet', { role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Set up Pocket Agent' }, dots, body));
    refreshBinders();
    renderModelHelpers(st.settings.providerId);
  }

  // ------------------------------------------------------------------ render
  function applyTheme(theme) {
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
    else delete document.documentElement.dataset.theme;
  }

  function render() {
    const st = app.state;
    if (!st) return;
    applyTheme(st.settings.theme);
    renderStatusBar(st);
    if (app.page === 'assistant') renderAssistant(st);
    if (app.page === 'connections') renderConnections(st);
    if (app.page === 'approvals') renderApprovals(st);
    if (app.page === 'settings') renderProviders(st);
    if (app.page === 'activity') renderLogs();
    refreshBinders();
    if (!st.setupComplete && !app.setupDismissed && !app.setupOpen) openSetup(0);
    else if (app.setupOpen) renderSetup();
  }

  // ------------------------------------------------------------------ messages from the host
  function onMessage(msg) {
    if (!msg || typeof msg !== 'object') return;
    const data = msg.data;
    switch (msg.type) {
      case 'state':
        app.state = data;
        render();
        break;
      case 'history':
        app.chat = data.chat || [];
        app.steps = new Map((data.steps || []).map(s => [s.id, s]));
        app.logs = data.logs || [];
        renderMessages();
        renderSteps();
        renderLogs();
        render();
        break;
      case 'chat':
        app.chat.push(data);
        if (app.chat.length > 500) app.chat.splice(0, app.chat.length - 500);
        renderMessages();
        if (app.state) renderAssistant(app.state);
        break;
      case 'step':
        app.steps.set(data.id, Object.assign(app.steps.get(data.id) || {}, data));
        renderSteps();
        break;
      case 'log':
        app.logs.push(data);
        if (app.logs.length > 1500) app.logs.splice(0, app.logs.length - 1500);
        if (app.page === 'activity') renderLogs();
        break;
      case 'level':
        app.level = app.level * 0.4 + data.value * 0.6;
        for (const el of document.querySelectorAll('.orb')) el.style.setProperty('--level', app.level.toFixed(3));
        for (const m of [settings.micMeter, setup.meter]) if (m) m.style.width = Math.round(app.level * 100) + '%';
        break;
      case 'toast':
        toast(data.kind, data.message);
        break;
      case 'models':
        app.models[data.providerId] = { models: data.models || [], error: data.error, fresh: true };
        renderModelHelpers(data.providerId);
        break;
      case 'providerTest':
        app.providerTests[data.providerId] = Object.assign({ fresh: true }, data);
        renderModelHelpers(data.providerId);
        break;
      case 'secret':
        app.revealed = { local: data.local, public: data['public'], until: Date.now() + 20000 };
        if (app.state) renderConnections(app.state);
        setTimeout(() => app.state && renderConnections(app.state), 20500);
        break;
      case 'transcript':
        for (const el of [settings.transcript, setup.transcript]) {
          if (!el) continue;
          el.className = 'result ' + (data.text ? 'ok' : 'bad');
          setText(el, data.text ? `Heard: “${data.text}”` : 'Nothing was recognised. Check the microphone and try again.');
        }
        break;
      case 'cleared':
        app.chat = [];
        for (const [id, s] of app.steps) if (s.source !== 'mcp') app.steps.delete(id);
        renderMessages();
        renderSteps();
        break;
    }
  }

  if (webview) webview.addEventListener('message', e => onMessage(e.data));

  // ------------------------------------------------------------------ start
  buildShell();
  buildAssistant();
  buildActivity();
  buildConnections();
  buildApprovals();
  buildSettings();
  go('assistant');
  renderMessages();
  renderSteps();
  setInterval(() => { tickCountdown(); updatePromptTimers(); }, 1000);
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') { const lb = $('.lightbox'); if (lb) lb.remove(); }
    if (e.ctrlKey && e.key.toLowerCase() === 'k') { e.preventDefault(); go('assistant'); composer.input.focus(); }
  });
  send('ready');
})();
