import { BROWSER_ID, CONFIG_URL, EVENT_URL } from './config.js';

const BLUR_URL = EVENT_URL.replace('/event', '/blur');
const AUDIO_SYNC_URL = EVENT_URL.replace('/event', '/audio-sync');
const MEDIA_URL = EVENT_URL.replace('/event', '/media');

// ---------- Focus guard ----------

async function isBrowserFocused() {
  try {
    const w = await chrome.windows.getLastFocused();
    return !!w && w.focused === true;
  } catch (_) {
    return false;
  }
}

// ---------- Idle Interval Setup ----------

async function setupIdleInterval() {
  let timeout = 180;
  try {
    if (chrome.storage && chrome.storage.session) {
      const data = await chrome.storage.session.get('idleTimeoutSeconds');
      if (data && typeof data.idleTimeoutSeconds === 'number' && data.idleTimeoutSeconds > 0) {
        timeout = data.idleTimeoutSeconds;
      } else {
        try {
          const res = await fetch(CONFIG_URL);
          if (res.ok) {
            const config = await res.json();
            if (config && typeof config.IdleTimeoutSeconds === 'number' && config.IdleTimeoutSeconds > 0) {
              timeout = config.IdleTimeoutSeconds;
            }
          }
        } catch (_) {}
        await chrome.storage.session.set({ idleTimeoutSeconds: timeout });
      }
    } else {
      try {
        const res = await fetch(CONFIG_URL);
        if (res.ok) {
          const config = await res.json();
          if (config && typeof config.IdleTimeoutSeconds === 'number' && config.IdleTimeoutSeconds > 0) {
            timeout = config.IdleTimeoutSeconds;
          }
        }
      } catch (_) {}
    }
  } catch (_) {
    timeout = 180;
  }

  try {
    chrome.idle.setDetectionInterval(timeout);
  } catch (_) {
    try {
      chrome.idle.setDetectionInterval(180);
    } catch (_) {}
  }
}

// ---------- HTTP helpers ----------

async function sendEvent(url, title, audible, isAudioOnly = false, isAudioStop = false) {
  try {
    await fetch(EVENT_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        Browser: BROWSER_ID,
        Url: url,
        Title: title,
        Audible: audible,
        IsAudioOnly: isAudioOnly,
        IsAudioStop: isAudioStop
      })
    });
  } catch (_) {
    // Service might be down, ignore
  }
}

async function sendBlur() {
  try {
    await fetch(BLUR_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Browser: BROWSER_ID })
    });
  } catch (_) {}
}

// Query all currently audible tabs, map to hostnames, and POST /audio-sync.
async function sendAudioSync() {
  try {
    const tabs = await chrome.tabs.query({ audible: true });
    const domains = tabs
      .filter(t => t.url && t.url.startsWith('http'))
      .map(t => {
        try { return new URL(t.url).hostname; } catch (_) { return null; }
      })
      .filter(Boolean);

    await fetch(AUDIO_SYNC_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Browser: BROWSER_ID, Domains: domains })
    });
  } catch (_) {}
}

// Query the active tab of the focused window; Playing = tab.audible === true.
// POST /media { Source: "extension", Playing: bool, Process: BROWSER_ID }
async function sendMedia() {
  try {
    let playing = false;
    if (await isBrowserFocused()) {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      if (tab && tab.audible === true) {
        playing = true;
      }
    }

    await fetch(MEDIA_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        Source: 'extension',
        Playing: playing,
        Process: BROWSER_ID
      })
    });
  } catch (_) {}
}

// ---------- Active-tab helpers ----------

async function sendActiveTab() {
  if (!(await isBrowserFocused())) {
    await sendBlur();
    return;
  }
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (tab && tab.url && tab.url.startsWith('http')) {
      await sendEvent(tab.url, tab.title, tab.audible);
    } else {
      await sendBlur();
    }
  } catch (_) {
    await sendBlur();
  }
}

async function sendActiveTabOfWindow(windowId) {
  if (!(await isBrowserFocused())) {
    await sendBlur();
    return;
  }
  try {
    const [tab] = await chrome.tabs.query({ active: true, windowId });
    if (tab && tab.url && tab.url.startsWith('http')) {
      await sendEvent(tab.url, tab.title, tab.audible);
    } else {
      await sendBlur();
    }
  } catch (_) {
    await sendBlur();
  }
}

// ---------- 30 s Alarm ----------

const AUDIO_HEARTBEAT_ALARM = 'audio-heartbeat';
chrome.alarms.create(AUDIO_HEARTBEAT_ALARM, { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === AUDIO_HEARTBEAT_ALARM) {
    await sendAudioSync();
    if (await isBrowserFocused()) {
      await sendActiveTab();
    }
    await sendMedia();
  }
});

// ---------- Idle / lock detection ----------

chrome.idle.onStateChanged.addListener(async (state) => {
  if (state === 'active') {
    await sendActiveTab();
  }
  await sendMedia();
});

// ---------- Window focus changes ----------

chrome.windows.onFocusChanged.addListener(async (windowId) => {
  if (windowId === chrome.windows.WINDOW_ID_NONE) {
    await sendBlur();
  } else {
    await sendActiveTabOfWindow(windowId);
  }
  await sendMedia();
});

// ---------- Tab activation ----------

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  if (!(await isBrowserFocused())) {
    await sendBlur();
    await sendMedia();
    return;
  }
  try {
    const tab = await chrome.tabs.get(activeInfo.tabId);
    if (tab && tab.url && tab.url.startsWith('http')) {
      await sendEvent(tab.url, tab.title, tab.audible);
    } else {
      await sendBlur();
    }
  } catch (_) {
    await sendBlur();
  }
  await sendMedia();
});

// ---------- Tab updates (URL/title/audible changes) ----------

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  // Call sendAudioSync() whenever changeInfo.audible !== undefined, independent of the title/url branch
  if (changeInfo.audible !== undefined) {
    await sendAudioSync();
    await sendMedia();
  }

  // Stateless "active and focused" check for URL or title change
  if (changeInfo.url || changeInfo.title) {
    try {
      if (tab.active === true) {
        const lastFocused = await chrome.windows.getLastFocused();
        if (lastFocused && lastFocused.focused === true && lastFocused.id === tab.windowId) {
          if (tab.url && tab.url.startsWith('http')) {
            await sendEvent(tab.url, tab.title, tab.audible);
          } else {
            await sendBlur();
          }
          await sendMedia();
        }
      }
    } catch (_) {}
  }
});

// ---------- Tab removal ----------

chrome.tabs.onRemoved.addListener(async () => {
  await sendAudioSync();
  await sendMedia();
});

// ---------- Service-worker startup ----------

(async () => {
  await setupIdleInterval();
  await sendAudioSync();
  if (await isBrowserFocused()) {
    await sendActiveTab();
  }
  await sendMedia();
})();
