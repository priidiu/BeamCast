// BeamCast Sync — MV3 service worker (RHI-173)
// chrome.alarms survives SW sleep; tabs.query needs host_permissions on the tab URL.

const METRICS_URL = "http://localhost:46382/metrics";
const ALARM = "beamcast-poll";
let latestMetrics = null;

async function fetchMetrics() {
  try {
    const resp = await fetch(METRICS_URL);
    if (!resp.ok) throw new Error("http " + resp.status);
    latestMetrics = await resp.json();
    updateBadge(latestMetrics.isStreaming ? "streaming" : "idle");
    await broadcastToTabs(latestMetrics);
  } catch {
    latestMetrics = null;
    updateBadge("disconnected");
  }
}

function updateBadge(state) {
  const colors = { streaming: "#2196F3", idle: "#9E9E9E", disconnected: "#F44336" };
  const texts = { streaming: "ON", idle: "", disconnected: "OFF" };
  chrome.action.setBadgeBackgroundColor({ color: colors[state] || "#9E9E9E" });
  chrome.action.setBadgeText({ text: texts[state] || "" });
}

async function broadcastToTabs(metrics) {
  const msg = { type: "beamcast-metrics", metrics };
  const tabs = await chrome.tabs.query({
    url: ["*://*.youtube.com/*", "*://*.netflix.com/*", "*://*.youtube-nocookie.com/*"],
  });
  for (const tab of tabs) {
    if (!tab.id) continue;
    chrome.tabs.sendMessage(tab.id, msg).catch(() => {});
  }
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.type === "get-metrics") {
    sendResponse({ metrics: latestMetrics });
    return true;
  }
});

chrome.alarms.onAlarm.addListener((a) => {
  if (a.name === ALARM) fetchMetrics();
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(ALARM, { periodInMinutes: 1 / 60 }); // ~1s (Chrome min often 0.016–1)
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(ALARM, { periodInMinutes: 1 / 60 });
});

fetchMetrics();
chrome.alarms.create(ALARM, { periodInMinutes: 1 / 60 });
