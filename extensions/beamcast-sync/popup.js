// BeamCast Sync — popup.js
// Shows connection status and latency metrics from BeamCast.

const statusEl = document.getElementById("status");
const statusText = document.getElementById("statusText");
const totalLatency = document.getElementById("totalLatency");
const networkLatency = document.getElementById("networkLatency");
const audioLatency = document.getElementById("audioLatency");
const wasapiBuffer = document.getElementById("wasapiBuffer");
const modeEl = document.getElementById("mode");

function update(m) {
  if (!m || !m.isStreaming) {
    statusEl.className = "status disconnected";
    statusText.textContent = "Disconnected";
    totalLatency.textContent = "—";
    networkLatency.textContent = "—";
    audioLatency.textContent = "—";
    wasapiBuffer.textContent = "—";
    modeEl.textContent = "—";
    return;
  }

  statusEl.className = "status streaming";
  statusText.textContent = "Streaming";

  totalLatency.textContent = `${Math.round(m.delayMs || m.totalLatencyMs)}ms`;
  networkLatency.textContent = m.networkLatencyMs >= 0 ? `${Math.round(m.networkLatencyMs)}ms` : "—";
  audioLatency.textContent = `${Math.round(m.audioLatencyMs)}ms`;
  wasapiBuffer.textContent = `${m.wasapiBufferMs}ms`;
  modeEl.textContent = m.realTimeMode ? "⚡ RealTime" : "Normal";
}

// Pobierz metryki z background.js
chrome.runtime.sendMessage({ type: "get-metrics" }, (response) => {
  if (response && response.metrics) {
    update(response.metrics);
  }
});

// Listen for updates
chrome.runtime.onMessage.addListener((msg) => {
  if (msg.type === "beamcast-metrics") {
    update(msg.metrics);
  }
});

const DEFAULT_HOSTS = ["youtube.com", "youtu.be", "youtube-nocookie.com"];

function parseHost(raw) {
  const s = (raw || "").trim().toLowerCase();
  if (!s) return "";
  try {
    const u = new URL(s.includes("://") ? s : "https://" + s);
    return u.hostname.replace(/^www\./, "");
  } catch {
    return s.replace(/^www\./, "").split("/")[0];
  }
}

function renderHosts(hosts) {
  const ul = document.getElementById("hostList");
  ul.replaceChildren();
  hosts.forEach((h) => {
    const li = document.createElement("li");
    const name = document.createElement("span");
    name.textContent = h;
    const rm = document.createElement("button");
    rm.type = "button";
    rm.textContent = "×";
    rm.title = "Remove";
    rm.addEventListener("click", () => {
      const next = hosts.filter((x) => x !== h);
      chrome.storage.local.set({ allowedHosts: next });
    });
    li.append(name, rm);
    ul.append(li);
  });
}

chrome.storage.local.get({ allowedHosts: DEFAULT_HOSTS }, (s) => {
  const hosts = Array.isArray(s.allowedHosts) ? s.allowedHosts : DEFAULT_HOSTS.slice();
  renderHosts(hosts);
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes.allowedHosts) return;
  const hosts = changes.allowedHosts.newValue;
  renderHosts(Array.isArray(hosts) ? hosts : DEFAULT_HOSTS.slice());
});

document.getElementById("hostAdd").addEventListener("click", addHost);
document.getElementById("hostInput").addEventListener("keydown", (e) => {
  if (e.key === "Enter") addHost();
});

function addHost() {
  const input = document.getElementById("hostInput");
  const host = parseHost(input.value);
  if (!host) return;
  chrome.storage.local.get({ allowedHosts: DEFAULT_HOSTS }, (s) => {
    const hosts = Array.isArray(s.allowedHosts) ? s.allowedHosts.slice() : DEFAULT_HOSTS.slice();
    if (!hosts.includes(host)) hosts.push(host);
    chrome.storage.local.set({ allowedHosts: hosts });
    input.value = "";
  });
}

const cornerEl = document.getElementById("showCornerHud");
chrome.storage.local.get({ showCornerHud: true }, (s) => {
  cornerEl.checked = s.showCornerHud !== false;
});
cornerEl.addEventListener("change", () => {
  chrome.storage.local.set({ showCornerHud: cornerEl.checked });
});
