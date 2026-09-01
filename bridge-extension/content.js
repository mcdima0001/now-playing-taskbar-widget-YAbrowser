// Читает из вкладки то, чего Яндекс.Браузер не отдаёт в SMTC: метаданные
// navigator.mediaSession и состояние самого <video>/<audio>. Работает в
// изолированном мире - DOM и mediaSession общие со страницей, отдельный
// инжект в MAIN не нужен.
(() => {
  // Скрипт приходит двумя путями - из manifest и доинжектом в уже
  // открытые вкладки; второй заход должен уйти молча
  if (window.__npwBridgeLoaded) return;
  window.__npwBridgeLoaded = true;

  const VERSION = chrome.runtime.getManifest().version;

  const TICK_MS = 1000;
  const HEARTBEAT_MS = 3000;

  // Сайты со своим плеером: у них нет медиаэлемента или он не управляется
  // напрямую, поэтому жмём родные кнопки. Классы у Яндекса собираются
  // сборщиком (VibePlayerControls_playButton__vnoer), хвост-хеш меняется от
  // релиза к релизу - цепляемся за устойчивую часть через [class*=]
  const one = sel => document.querySelector(sel);

  // У Яндекс Музыки две раскладки плеербара, и они живут одновременно:
  // старая "вайбовая" (VibePlayerBar*) и новая
  // (PlayerBarDesktopWithBackgroundProgressBar*). Классы разные, поэтому
  // сначала находим сам плеербар, а кнопки ищем уже внутри него - это ещё и
  // отсекает одноимённые кнопки у треков в списках
  function yandexBar() {
    return one('[class*="PlayerBarDesktopWithBackgroundProgressBar_playerBar"]') ||
           one('[class*="PlayerBarDesktop"]') ||
           one('[class*="VibePlayerBar"]') ||
           one('[class*="VibePlayerbarMeta_root"]');
  }

  const SITES = [
    {
      host: /(^|\.)music\.yandex\.(ru|com|by|kz|uz)$/,
      ctl: () => {
        const bar = yandexBar();
        const inBar = sel => (bar ? bar.querySelector(sel) : null);
        const skip = bar ? [...bar.querySelectorAll('button[class*="skipButton"]')] : [];
        return {
          bar,
          // Подписи кнопок переживают смену раскладки лучше классов, но их
          // обязательно надо ограничивать плеербаром
          play: inBar('button[class*="VibePlayerControls_playButton"]') ||
                inBar('button[aria-label="Пауза"]') ||
                inBar('button[aria-label="Воспроизведение"]') ||
                inBar('button[aria-label="Воспроизвести"]'),
          prev: inBar('button[aria-label="Предыдущая песня"]') || skip[0] || null,
          next: inBar('button[aria-label="Следующая песня"]') || skip[1] || null,
          like: inBar('button[aria-label="Нравится"]'),
        };
      },
    },
    {
      host: /(^|\.)youtube\.com$/,
      ctl: () => ({ play: one('.ytp-play-button'), next: one('.ytp-next-button'), prev: one('.ytp-prev-button') }),
    },
    {
      host: /(^|\.)open\.spotify\.com$/,
      ctl: () => ({
        play: one('[data-testid="control-button-playpause"]'),
        next: one('[data-testid="control-button-skip-forward"]'),
        prev: one('[data-testid="control-button-skip-back"]'),
        like: one('[data-testid="now-playing-widget"] [data-testid="add-button"]'),
      }),
    },
    {
      host: /(^|\.)soundcloud\.com$/,
      ctl: () => ({ play: one('.playControl'), next: one('.skipControl__next'), prev: one('.skipControl__previous') }),
    },
  ];

  const site = SITES.find(s => s.host.test(location.hostname)) || null;

  function controls() {
    if (!site) return {};
    try { return site.ctl() || {}; } catch { return {}; }
  }

  let lastSent = 0;
  let lastKey = '';
  let timer = null;

  // Играющий элемент; если играющих нет - последний, который что-то проиграл
  // (нужно, чтобы на паузе виджет не гас мгновенно)
  function pickMedia() {
    const all = [...document.querySelectorAll('video, audio')];
    if (!all.length) return null;
    const live = all.filter(e => !e.paused && !e.ended && e.readyState >= 2 && !e.muted && e.volume > 0);
    const pool = live.length ? live : all.filter(e => e.currentTime > 0 && !e.ended);
    if (!pool.length) return null;
    // Самый длинный - обычно основной контент, а не рекламная вставка/превью
    pool.sort((a, b) => (b.duration || 0) - (a.duration || 0));
    return pool[0];
  }

  function metadata() {
    try {
      return (navigator.mediaSession && navigator.mediaSession.metadata) || null;
    } catch {
      return null;
    }
  }

  function biggestArt(meta) {
    if (!meta || !meta.artwork || !meta.artwork.length) return '';
    let best = '', bestPx = -1;
    for (const a of meta.artwork) {
      const px = parseInt(String(a.sizes || '0x0').split('x')[0], 10) || 0;
      if (px >= bestPx) { bestPx = px; best = a.src || ''; }
    }
    return best;
  }

  // Заголовок вкладки как запасной вариант: у многих сайтов mediaSession нет
  function fallbackTitle() {
    return (document.title || '').replace(/\s*[-–—|]\s*(YouTube|Яндекс Музыка|SoundCloud)\s*$/i, '').trim();
  }

  function playbackState() {
    try { return (navigator.mediaSession && navigator.mediaSession.playbackState) || 'none'; }
    catch { return 'none'; }
  }

  // У плеера без DOM-элемента позиция всё же видна: полоса прогресса - это
  // слайдер с aria-valuenow/aria-valuemax в секундах. Слайдер громкости
  // отсекаем по максимуму (у него он равен 1)
  function sliderProgress() {
    let best = null;
    for (const e of document.querySelectorAll('[role="slider"], input[type="range"]')) {
      const now = parseFloat(e.getAttribute('aria-valuenow') ?? e.value);
      const max = parseFloat(e.getAttribute('aria-valuemax') ?? e.max);
      if (!isFinite(now) || !isFinite(max) || max <= 5) continue;
      if (!best || max > best.duration) best = { position: now, duration: max, el: e };
    }
    return best;
  }

  // Полоса прогресса у Яндекса - React-контролируемый <input type="range">.
  // Присвоение value в обход прототипа React не замечает (у него свой
  // valueTracker), поэтому зовём нативный сеттер и сами шлём input/change
  function setRangeValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (setter) setter.call(input, String(value));
    else input.value = String(value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Для role="slider" (не input) остаётся имитация клика в нужную точку
  function clickSliderAt(prog, seconds) {
    const r = prog.el.getBoundingClientRect();
    if (!r.width) return;
    const x = r.left + r.width * Math.max(0, Math.min(1, seconds / prog.duration));
    const y = r.top + r.height / 2;
    const opts = { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 };
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
      const Ctor = type.startsWith('pointer') && window.PointerEvent ? PointerEvent : MouseEvent;
      prog.el.dispatchEvent(new Ctor(type, opts));
    }
  }

  function seekTo(prog, seconds) {
    const s = Math.max(0, Math.min(seconds, prog.duration));
    if (prog.el.tagName === 'INPUT') setRangeValue(prog.el, Math.round(s));
    else clickSliderAt(prog, s);
  }

  // Состояние избранного сайт держит в aria-pressed кнопки; null означает
  // "кнопки нет" - виджет тогда показывает нейтральный плюс
  function likeState() {
    const b = controls().like;
    if (!b) return { canLike: false, liked: null };
    const pressed = b.getAttribute('aria-pressed');
    return { canLike: true, liked: pressed === null ? null : pressed === 'true' };
  }

  // Пометка о ненормативной лексике. В mediaSession её нет, поэтому смотрим
  // вёрстку: у Яндекса это svg с классом ExplicitMarkIcon_explicitMark и
  // подписью "Возрастное ограничение 18+". Такие же значки стоят у треков в
  // списках, поэтому ищем только внутри плеербара
  function explicitMark() {
    const bar = controls().bar;
    if (!bar) return false;
    try {
      // Только сам значок: контейнер Meta_explicitMarkContainer отрисован
      // всегда, даже у треков без пометки, и по нему она загоралась везде
      return !!bar.querySelector(
        'svg[class*="xplicitMark"], [aria-label*="озрастное ограничение"]');
    } catch {
      return false;
    }
  }

  function snapshot() {
    const el = pickMedia();
    const meta = metadata();

    // Яндекс.Музыка в ЯБраузере играет встроенным плеером: в документе нет
    // ни <audio>, ни <video>, зато mediaSession заполнен. Позиции в нём нет
    // (setPositionState только пишется), поэтому шкала останется пустой
    if (!el) {
      if (!meta || !meta.title) return null;
      const prog = sliderProgress();
      return {
        playing: playbackState() === 'playing',
        title: meta.title,
        artist: meta.artist || location.hostname.replace(/^www\./, ''),
        album: meta.album || '',
        art: biggestArt(meta),
        position: prog ? prog.position : 0,
        duration: prog ? prog.duration : 0,
        canSeek: !!prog,
        canNext: !!controls().next,
        canPrev: !!controls().prev,
        explicit: explicitMark(),
        v: VERSION,
        ...likeState(),
        host: location.hostname,
      };
    }

    const duration = isFinite(el.duration) ? el.duration : 0;
    // Короткие звуки (уведомления, автоплей-превью) - не музыка
    if (duration > 0 && duration < 5) return null;
    return {
      playing: !el.paused && !el.ended,
      title: (meta && meta.title) || fallbackTitle(),
      artist: (meta && meta.artist) || location.hostname.replace(/^www\./, ''),
      album: (meta && meta.album) || '',
      art: biggestArt(meta),
      position: el.currentTime || 0,
      duration,
      canSeek: duration > 0 && !!el.seekable && el.seekable.length > 0,
      canNext: !!controls().next,
      canPrev: !!controls().prev,
      explicit: explicitMark(),
      v: VERSION,
      ...likeState(),
      host: location.hostname,
    };
  }

  function key(s) {
    return [s.playing, s.title, s.artist, s.art, Math.round(s.duration), s.canNext, s.canPrev, s.liked, s.explicit].join('|');
  }

  function send(state, why) {
    lastSent = Date.now();
    try {
      chrome.runtime.sendMessage({ type: 'state', state, why, ts: Date.now() });
    } catch {
      // расширение перезагружено/выгружено - вкладка это переживёт молча
      stop();
    }
  }

  function tick() {
    const s = snapshot();
    if (!s) {
      if (lastKey) {
        lastKey = '';
        try { chrome.runtime.sendMessage({ type: 'gone' }); } catch {}
      }
      return;
    }
    const k = key(s);
    if (k !== lastKey) {
      lastKey = k;
      send(s, 'change');
    } else if (Date.now() - lastSent >= HEARTBEAT_MS) {
      // Позицию виджет интерполирует сам; пульс нужен, чтобы он видел,
      // что вкладка жива, и чтобы поправить дрейф после перемоток
      send(s, 'beat');
    }
  }

  function stop() {
    if (timer) { clearInterval(timer); timer = null; }
  }

  chrome.runtime.onMessage.addListener((msg, _sender, respond) => {
    const el = pickMedia();
    const c = controls();
    if (msg && msg.cmd === 'playpause') {
      // Кнопка сайта надёжнее элемента: она же переключает и встроенный плеер
      if (c.play) c.play.click();
      else if (el) { if (el.paused) el.play().catch(() => {}); else el.pause(); }
    } else if (msg && msg.cmd === 'seek' && isFinite(msg.pos)) {
      if (el) {
        try { el.currentTime = Math.max(0, Math.min(msg.pos, el.duration || msg.pos)); } catch {}
      } else {
        const prog = sliderProgress();
        if (prog) seekTo(prog, msg.pos);
      }
    } else if (msg && (msg.cmd === 'next' || msg.cmd === 'prev')) {
      const b = msg.cmd === 'next' ? c.next : c.prev;
      if (b) b.click();
    } else if (msg && msg.cmd === 'like') {
      if (c.like) c.like.click();
    }
    // Состояние после команды - сразу, без ожидания тика
    const s = snapshot();
    if (s) { lastKey = key(s); send(s, 'cmd'); }
    respond && respond({ ok: true });
    return false;
  });

  timer = setInterval(tick, TICK_MS);
  tick();
})();
