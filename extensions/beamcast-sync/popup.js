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
