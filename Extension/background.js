import { BROWSER_ID, CONFIG_URL, EVENT_URL } from './config.js';

const BLUR_URL = EVENT_URL.replace('/event', '/blur');

let includeDomains = [];
let excludeDomains = [];
let activeTabId = null;

// ---------- Config ----------

async function fetchConfig() {
  try {
    const res = await fetch(CONFIG_URL);
    if (res.ok) {
      const config = await res.json();
      includeDomains = config.IncludeDomains || [];
      excludeDomains = config.ExcludeDomains || [];
    }
  } catch (err) {
    console.error('Failed to fetch tracker config', err);
  }
}

fetchConfig();
setInterval(fetchConfig, 5 * 60 * 1000);

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

// ---------- Active-tab helpers ----------

async function sendActiveTab() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (tab && tab.url && tab.url.startsWith('http')) {
      activeTabId = tab.id;
      await sendEvent(tab.url, tab.title, tab.audible);
    }
  } catch (_) {}
}

async function sendActiveTabOfWindow(windowId) {
  try {
    const [tab] = await chrome.tabs.query({ active: true, windowId });
    if (tab && tab.url && tab.url.startsWith('http')) {
      activeTabId = tab.id;
      await sendEvent(tab.url, tab.title, tab.audible);
    }
  } catch (_) {}
}

// ---------- Service-worker startup ----------
// Send audio-start for every currently audible tab.
(async () => {
  try {
    const audibleTabs = await chrome.tabs.query({ audible: true });
    for (const tab of audibleTabs) {
      if (tab.url && tab.url.startsWith('http')) {
        await sendEvent(tab.url, tab.title, true, true, false);
      }
    }
  } catch (_) {}
})();

// Set idle detection interval to 60 seconds.
chrome.idle.setDetectionInterval(60);

// ---------- Idle / lock detection ----------

chrome.idle.onStateChanged.addListener(async (state) => {
  if (state === 'idle' || state === 'locked') {
    await sendBlur();
  } else if (state === 'active') {
    await sendActiveTab();
  }
});

// ---------- Window focus changes ----------

chrome.windows.onFocusChanged.addListener(async (windowId) => {
  if (windowId === chrome.windows.WINDOW_ID_NONE) {
    await sendBlur();
  } else {
    await sendActiveTabOfWindow(windowId);
  }
});

// ---------- Tab activation ----------

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  activeTabId = activeInfo.tabId;
  try {
    const tab = await chrome.tabs.get(activeTabId);
    if (tab && tab.url && tab.url.startsWith('http')) {
      await sendEvent(tab.url, tab.title, tab.audible);
    }
  } catch (_) {}
});

// ---------- Tab updates (URL/title/audible changes) ----------

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (!tab.url || !tab.url.startsWith('http')) return;

  if (tabId === activeTabId) {
    // Active tab: report focus events AND audible changes.
    if (changeInfo.url || changeInfo.title) {
      await sendEvent(tab.url, tab.title, tab.audible);
    } else if (changeInfo.audible !== undefined) {
      // Active tab toggled audio: use IsAudioOnly so the server opens/closes an audio session
      // without replacing the browser (focus) session.
      await sendEvent(tab.url, tab.title, changeInfo.audible, true, !changeInfo.audible);
    }
  } else {
    // Background tab: only care about audible changes.
    if (changeInfo.audible !== undefined) {
      await sendEvent(tab.url, tab.title, changeInfo.audible, true, !changeInfo.audible);
    }
  }
});
