import { BROWSER_ID, CONFIG_URL, EVENT_URL } from './config.js';

let includeDomains = [];
let excludeDomains = [];
let activeTabId = null;

// Fetch config every 5 minutes
async function fetchConfig() {
  try {
    const res = await fetch(CONFIG_URL);
    if (res.ok) {
      const config = await res.json();
      includeDomains = config.IncludeDomains || [];
      excludeDomains = config.ExcludeDomains || [];
    }
  } catch (err) {
    console.error("Failed to fetch tracker config", err);
  }
}

fetchConfig();
setInterval(fetchConfig, 5 * 60 * 1000);

async function sendEvent(url, title, audible, isAudioOnly = false, isAudioStop = false) {
  try {
    const payload = {
      Browser: BROWSER_ID,
      Url: url,
      Title: title,
      Audible: audible,
      IsAudioOnly: isAudioOnly,
      IsAudioStop: isAudioStop
    };
    
    await fetch(EVENT_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
  } catch (err) {
    // Service might be down, ignore
  }
}

chrome.tabs.onActivated.addListener(async (activeInfo) => {
  activeTabId = activeInfo.tabId;
  try {
    const tab = await chrome.tabs.get(activeTabId);
    if (tab && tab.url && tab.url.startsWith("http")) {
      await sendEvent(tab.url, tab.title, tab.audible);
    }
  } catch (err) {}
});

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  if (tab.url && tab.url.startsWith("http")) {
    if (tabId === activeTabId) {
      // Focus or title change on active tab
      if (changeInfo.url || changeInfo.title || changeInfo.audible !== undefined) {
        await sendEvent(tab.url, tab.title, tab.audible);
      }
    } else {
      // Background tab changed audible state
      if (changeInfo.audible !== undefined) {
        await sendEvent(tab.url, tab.title, changeInfo.audible, true, !changeInfo.audible);
      }
    }
  }
});
