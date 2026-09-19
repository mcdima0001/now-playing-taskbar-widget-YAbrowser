// Собирает состояния со всех вкладок, выбирает текущую и держит WebSocket
// к виджету. Порт слушает только 127.0.0.1, наружу ничего не уходит.
const PORT = 45219;
const STALE_MS = 30000;
// Как часто сами спрашиваем вкладку о состоянии. Пульс из вкладки не годится:
// браузер душит таймеры фоновой страницы, как только она замолкает, и на паузе
// отчёты приходят раз в минуту - виджет за это время успевает погаснуть
const PULL_MS = 4000;
// Вкладка может быть заморожена (экономия памяти) и не отвечать на опрос -
// это ещё не повод её хоронить, ждём минуту
const SILENCE_MS = 60000;
// Вкладка, из которой минуту не идёт звук, перестаёт претендовать на виджет.
// Без этого несколько открытых ютубов воевали за него: виджет мигал между
// ними, хотя звучал один. Признак берём у самого браузера (tab.audible), а не
// из флага проигрывания со страницы - у замьюченной или домолчавшей вкладки
// флаг вполне остаётся выставленным, и как раз они и воевали
const QUIET_MS = 60000;
// Исключение - Яндекс Музыка: у неё пауза живёт сколько угодно, виджет нарочно
// продолжает показывать трек, пока держится соединение
const KEEPS_PAUSED = /(^|\.)music\.yandex\.(ru|com|by|kz|uz)$/;

// "tabId:frameId" -> { state, ts, firstSeen, audible, lastAudibleAt,
//                      tabId, windowId, frameId }
const tabs = new Map();

function keepsPaused(v) {
  return KEEPS_PAUSED.test((v.state && v.state.host) || '');
}

// Кто сейчас звучит. Один запрос на все вкладки, а не по одному на каждую
async function refreshAudible() {
  let ids;
  try {
    ids = new Set((await chrome.tabs.query({ audible: true })).map(t => t.id));
  } catch {
    return; // прав нет или браузер закрывается - молча
  }
  const now = Date.now();
  for (const v of tabs.values()) {
    v.audible = ids.has(v.tabId);
    if (v.audible) v.lastAudibleAt = now;
  }
}

// Забыть вкладки, которые давно не звучат. Возвращает true, если кого-то убрали
function dropQuiet() {
  const now = Date.now();
  let dropped = false;
  for (const [k, v] of [...tabs]) {
    if (keepsPaused(v)) continue;
    // Только что найденной вкладке даём ту же минуту: звучит она или нет,
    // мы ещё не знаем - опрос мог не успеть пройти
    if (now - v.firstSeen < QUIET_MS) continue;
    if (now - v.lastAudibleAt <= QUIET_MS) continue;
    tabs.delete(k);
    dropped = true;
  }
  return dropped;
}
let ws = null;
let retry = 1000;
let lastSentKey = '';
let pullTimer = null;

function idOf(sender) {
  return `${sender.tab ? sender.tab.id : 0}:${sender.frameId || 0}`;
}

// Текущая: сначала та, что реально звучит, потом играющая по своим данным,
// при равенстве - с самым свежим отчётом
function current() {
  const now = Date.now();
  let best = null;
  for (const [k, v] of tabs) {
    if (now - v.ts > STALE_MS) { tabs.delete(k); continue; }
    if (!best) { best = v; continue; }
    // Звук важнее всего: пользователь слышит именно эту вкладку
    if (!!v.audible !== !!best.audible) { if (v.audible) best = v; continue; }
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

// Спрашиваем каждую известную вкладку напрямую: доставку сообщений throttling
// не трогает, в отличие от таймеров внутри страницы. Ответ вкладка присылает
// обычным state-сообщением, поэтому здесь важен только сам факт отказа
async function pull() {
  if (!tabs.size) return;
  // Сначала разбираемся, кто звучит, и выкидываем замолчавших - опрашивать и
  // учитывать их дальше незачем
  await refreshAudible();
  if (dropQuiet()) push(true);
  if (!tabs.size) return;
  const now = Date.now();
  for (const [k, v] of [...tabs]) {
    try {
      await chrome.tabs.sendMessage(v.tabId, { cmd: 'ping' }, { frameId: v.frameId });
      v.silentSince = 0;
    } catch {
      // Вкладки нет - забываем сразу; есть, но молчит - даём ей минуту
      let gone = false;
      try { await chrome.tabs.get(v.tabId); } catch { gone = true; }
      if (!gone) {
        if (!v.silentSince) v.silentSince = now;
        if (now - v.silentSince < SILENCE_MS) continue;
      }
      tabs.delete(k);
      push(true);
    }
  }
}

function startPull() {
  if (pullTimer) return;
  pullTimer = setInterval(pull, PULL_MS);
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
      // Показать источник звука: активировать вкладку и её окно. Без
      // drawAttention - он и был тем самым миганием на панели задач. Поднять
      // окно на передний план браузеру Windows всё равно не даёт (он фоновый
      // процесс), поэтому это делает виджет: отдаём ему заголовок вкладки,
      // по нему он найдёт нужное окно среди нескольких
      const target = cur;
      (async () => {
        try { await chrome.tabs.update(target.tabId, { active: true }); } catch {}
        if (target.windowId != null) {
          try { await chrome.windows.update(target.windowId, { focused: true }); } catch {}
        }
        let title = '';
        try { title = (await chrome.tabs.get(target.tabId)).title || ''; } catch {}
        if (ws && ws.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ type: 'focused', title }));
        }
      })();
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
    const prev = tabs.get(id);
    tabs.set(id, {
      state: msg.state,
      ts: Date.now(),
      // Отметки о звуке переносим со старой записи: их обновляет опрос, а
      // отчёты от вкладки приходят чаще и ничего о звуке не знают
      firstSeen: prev ? prev.firstSeen : Date.now(),
      audible: prev ? prev.audible : false,
      lastAudibleAt: prev ? prev.lastAudibleAt : 0,
      tabId: sender.tab ? sender.tab.id : 0,
      windowId: sender.tab ? sender.tab.windowId : null,
      frameId: sender.frameId || 0,
    });
    connect();
    startPull();
    push(false);
  }
  return false;
});

chrome.tabs.onRemoved.addListener(tabId => {
  for (const k of [...tabs.keys()]) if (k.startsWith(`${tabId}:`)) tabs.delete(k);
  push(true);
});

// Звук появился или пропал - узнаём сразу, не дожидаясь очередного опроса:
// переключение между вкладками должно быть мгновенным
chrome.tabs.onUpdated.addListener((tabId, info) => {
  if (info.audible === undefined) return;
  const now = Date.now();
  let touched = false;
  for (const v of tabs.values()) {
    if (v.tabId !== tabId) continue;
    v.audible = info.audible;
    if (info.audible) v.lastAudibleAt = now;
    touched = true;
  }
  if (touched) push(false);
});

// Service worker засыпает; будильник поднимает его и восстанавливает сокет
chrome.alarms.create('keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => { connect(); startPull(); pull(); });
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
startPull();
injectAll();
