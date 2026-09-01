// Собирает состояния со всех вкладок, выбирает текущую и держит WebSocket
// к виджету. Порт слушает только 127.0.0.1, наружу ничего не уходит.
const PORT = 45219;
const STALE_MS = 8000;

const tabs = new Map(); // "tabId:frameId" -> { state, ts, tabId, frameId }
let ws = null;
let retry = 1000;
let lastSentKey = '';

function idOf(sender) {
  return `${sender.tab ? sender.tab.id : 0}:${sender.frameId || 0}`;
}

// Текущая = играющая с самым свежим отчётом; если играющих нет - самая свежая
function current() {
  const now = Date.now();
  let best = null;
  for (const [k, v] of tabs) {
    if (now - v.ts > STALE_MS) { tabs.delete(k); continue; }
    if (!best) { best = v; continue; }
    const bp = best.state.playing, vp = v.state.playing;
    if (vp !== bp) { if (vp) best = v; continue; }
    if (v.ts > best.ts) best = v;
  }
  return best;
}

function push(force) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  const cur = current();
  if (!cur) {
    if (lastSentKey !== 'idle') {
      lastSentKey = 'idle';
      ws.send(JSON.stringify({ type: 'idle' }));
    }
    return;
  }
  const s = cur.state;
  const key = [s.playing, s.title, s.artist, s.art, Math.round(s.duration)].join('|');
  if (!force && key === lastSentKey) {
    // позиция всё равно нужна регулярно - виджет правит дрейф по ней
    ws.send(JSON.stringify({ type: 'state', ...s, ts: Date.now() }));
    return;
  }
  lastSentKey = key;
  ws.send(JSON.stringify({ type: 'state', ...s, ts: Date.now() }));
}

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  try {
    ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
  } catch {
    schedule();
    return;
  }
  ws.onopen = () => {
    retry = 1000;
    lastSentKey = '';
    push(true);
  };
  ws.onmessage = ev => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { return; }
    const cur = current();
    if (!cur || !msg || !msg.cmd) return;
    if (msg.cmd === 'focus') {
      // Показать источник звука: активировать вкладку и поднять её окно.
      // Виджет перед отправкой снимает запрет на смену активного окна,
      // иначе Windows разрешит браузеру только мигнуть на панели задач
      chrome.tabs.update(cur.tabId, { active: true }).catch(() => {});
      if (cur.windowId != null) {
        chrome.windows.update(cur.windowId, { focused: true, drawAttention: true }).catch(() => {});
      }
      return;
    }
    chrome.tabs.sendMessage(cur.tabId, msg, { frameId: cur.frameId }).catch(() => {});
  };
  ws.onclose = () => { ws = null; schedule(); };
  ws.onerror = () => { try { ws.close(); } catch {} };
}

function schedule() {
  // Виджет может быть не запущен - переподключаемся редко и без паники
  retry = Math.min(retry * 2, 15000);
  setTimeout(connect, retry);
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (!msg) return false;
  const id = idOf(sender);
  if (msg.type === 'gone') {
    tabs.delete(id);
    push(true);
  } else if (msg.type === 'state') {
    tabs.set(id, {
      state: msg.state,
      ts: Date.now(),
      tabId: sender.tab ? sender.tab.id : 0,
      windowId: sender.tab ? sender.tab.windowId : null,
      frameId: sender.frameId || 0,
    });
    connect();
    push(false);
  }
  return false;
});

chrome.tabs.onRemoved.addListener(tabId => {
  for (const k of [...tabs.keys()]) if (k.startsWith(`${tabId}:`)) tabs.delete(k);
  push(true);
});

// Service worker засыпает; будильник поднимает его и восстанавливает сокет
chrome.alarms.create('keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => connect());
// Контент-скрипты из манифеста попадают только во вкладки, открытые ПОСЛЕ
// установки. Вкладку с музыкой пользователь открыл раньше - доинжектим сами,
// иначе расширение молчит до перезагрузки страницы
async function injectAll() {
  try {
    const list = await chrome.tabs.query({ url: ['http://*/*', 'https://*/*'] });
    for (const t of list) {
      if (t.id == null) continue;
      chrome.scripting
        .executeScript({ target: { tabId: t.id, allFrames: true }, files: ['content.js'] })
        .catch(() => {}); // служебные страницы инжект запрещают - это норма
    }
  } catch {}
}

chrome.runtime.onStartup.addListener(() => { connect(); injectAll(); });
chrome.runtime.onInstalled.addListener(() => { connect(); injectAll(); });
connect();
injectAll();
