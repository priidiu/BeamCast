// BeamCast video delay — independent reimplementation of the Frame Sync idea
// (circular canvas ring + rVFC capture + rAF present, overlay on the video
// parent). Not a copy of maggch97/Frame-Sync (GPL-2.0).
// Audio of <video> is never paused (WASAPI loopback needs it).

(() => {
  "use strict";

  const MIN_VIDEO_PX = 80;
  const attached = new WeakMap();

  let delayMs = 0;
  let streaming = false;
  let overlay = null;

  class Slot {
    constructor(video) {
      this.canvas = document.createElement("canvas");
      this.video = video;
      this.ts = 0;
      this.fit();
    }
    fit() {
      const v = this.video;
      this.canvas.width = Math.max(1, v.videoWidth || 1);
      this.canvas.height = Math.max(1, v.videoHeight || 1);
      this.ctx = this.canvas.getContext("2d", { alpha: false });
    }
    grab(now) {
      this.ctx.drawImage(this.video, 0, 0, this.canvas.width, this.canvas.height);
      this.ts = now;
    }
  }

  class DelayPipe {
    constructor(video) {
      this.video = video;
      this.slots = [];
      this.cap = 0;
      this.delay = 0;
      this.on = false;
      this.written = 0;
      this.shown = -1;
      this.overlay = null;
      this.out = null;
      this._onCap = this._onCap.bind(this);
      this._onDraw = this._onDraw.bind(this);
      this._onResize = this._onResize.bind(this);
      this.lastW = 0;
      this.lastH = 0;
      this.lastL = 0;
      this.lastT = 0;
    }

    setDelay(ms) {
      this.delay = Math.max(0, ms);
    }

    _grow(n) {
      if (n < 2) n = 2;
      if (n === this.cap && this.slots.length === n) return;
      const keep = this.slots.filter((s) => s.ts).sort((a, b) => a.ts - b.ts);
      this.slots = keep.slice();
      while (this.slots.length < n) this.slots.push(new Slot(this.video));
      this.cap = n;
      this.written = keep.length;
      this.shown = -1;
    }

    start() {
      if (this.on) return;
      this.on = true;
      this.written = 0;
      this.shown = -1;
      if (this.cap < 10) this._grow(10);
      this._mount();
      this.video.requestVideoFrameCallback(this._onCap);
      requestAnimationFrame(this._onDraw);
    }

    stop() {
      this.on = false;
      if (this.overlay) {
        this.overlay.remove();
        this.overlay = null;
        this.out = null;
      }
      window.removeEventListener("resize", this._onResize);
      this.video.removeEventListener("resize", this._onResize);
    }

    _mount() {
      const v = this.video;
      const c = document.createElement("canvas");
      const st = getComputedStyle(v);
      c.style.position = "absolute";
      c.style.pointerEvents = "none";
      c.style.zIndex = st.zIndex && st.zIndex !== "auto" ? st.zIndex : "1";
      v.parentElement.appendChild(c);
      this.overlay = c;
      this.out = c.getContext("2d", { alpha: false });
      window.addEventListener("resize", this._onResize);
      v.addEventListener("resize", this._onResize);
      this._layout();
    }

    needsLayout() {
      const v = this.video;
      const c = this.overlay;
      if (!c) return false;
      return (
        c.width !== v.videoWidth ||
        c.height !== v.videoHeight ||
        this.lastW !== v.offsetWidth ||
        this.lastH !== v.offsetHeight ||
        this.lastL !== v.offsetLeft ||
        this.lastT !== v.offsetTop
      );
    }

    _layout() {
      const v = this.video;
      const c = this.overlay;
      if (!c || !v.videoWidth) return;
      c.width = v.videoWidth;
      c.height = v.videoHeight;
      let w = v.offsetWidth;
      let h = v.offsetHeight;
      let l = v.offsetLeft;
      let t = v.offsetTop;
      const va = v.videoWidth / v.videoHeight;
      const ca = w / Math.max(1, h);
      if (Math.abs(va / ca - 1) >= 0.02) {
        if (va > ca) {
          h = w / va;
          t = v.offsetTop + (v.offsetHeight - h) / 2;
        } else {
          w = h * va;
          l = v.offsetLeft + (v.offsetWidth - w) / 2;
        }
      }
      c.style.width = w + "px";
      c.style.height = h + "px";
      c.style.left = l + "px";
      c.style.top = t + "px";
      this.lastW = v.offsetWidth;
      this.lastH = v.offsetHeight;
      this.lastL = v.offsetLeft;
      this.lastT = v.offsetTop;
      for (const s of this.slots) s.fit();
    }

    _onResize() {
      this._layout();
    }

    _onCap(now) {
      if (!this.on) return;
      if (!this.cap) this._grow(10);
      const slot = this.slots[this.written % this.cap];
      if (slot.ts && now - slot.ts < Math.min(this.delay * 2, this.delay + 500)) {
        this._grow(Math.round(this.cap * 1.5));
      }
      try {
        this.slots[this.written % this.cap].grab(now);
      } catch {
        /* cross-origin: skip frame */
      }
      this.written++;
      this.video.requestVideoFrameCallback(this._onCap);
    }

    _onDraw(now) {
      if (!this.on) {
        return;
      }
      if (this.out && this.cap && this.written > 0) {
        while (true) {
          const nxt = (this.shown + 1) % this.cap;
          if (nxt === this.written % this.cap) break;
          const s = this.slots[nxt];
          if (!s || !s.ts) break;
          if (now - s.ts < this.delay) break;
          this.shown = nxt;
        }
        if (this.shown >= 0) {
          const f = this.slots[this.shown];
          try {
            this.out.drawImage(
              f.canvas,
              0,
              0,
              this.video.videoWidth,
              this.video.videoHeight
            );
          } catch {
            /* ignore */
          }
        }
      }
      requestAnimationFrame(this._onDraw);
    }
  }

  const ytpChipStyle = {
    display: "inline-flex",
    alignItems: "center",
    height: "100%",
    padding: "0 10px",
    margin: "0",
    fontSize: "13px",
    fontFamily: "Roboto, Arial, sans-serif",
    fontWeight: "500",
    color: "#eee",
    whiteSpace: "nowrap",
    pointerEvents: "none",
    background: "transparent",
    lineHeight: "1",
    position: "static",
    bottom: "auto",
    right: "auto",
    zIndex: "auto",
    borderRadius: "0",
  };

  const cornerStyle = {
    display: "block",
    position: "fixed",
    bottom: "12px",
    right: "12px",
    zIndex: "2147483647",
    padding: "8px 12px",
    margin: "0",
    fontSize: "12px",
    fontFamily: "system-ui, sans-serif",
    fontWeight: "500",
    color: "#fff",
    whiteSpace: "nowrap",
    pointerEvents: "none",
    background: "rgba(0,0,0,0.72)",
    borderRadius: "8px",
    height: "auto",
    lineHeight: "1.3",
  };

  function applyStyle(el, spec) {
    Object.assign(el.style, spec);
  }

  function hud(text) {
    if (!overlay || !overlay.isConnected) {
      overlay = document.createElement("div");
      overlay.id = "beamcast-ytp-chip";
    }
    placeHud();
    if (overlay.textContent !== text) overlay.textContent = text;
  }

  function placeHud() {
    if (!overlay) return;
    const bar = document.querySelector(".ytp-right-controls");
    if (bar) {
      applyStyle(overlay, ytpChipStyle);
      if (overlay.parentElement !== bar || overlay !== bar.firstElementChild) {
        bar.insertBefore(overlay, bar.firstChild);
      }
      return;
    }
    applyStyle(overlay, cornerStyle);
    if (overlay.parentElement !== document.documentElement && overlay.parentElement !== document.body) {
      (document.body || document.documentElement).appendChild(overlay);
    }
  }

  function apply(m) {
    if (!m || !m.isStreaming) {
      streaming = false;
      delayMs = 0;
      hud("BC idle");
      document.querySelectorAll("video").forEach((v) => {
        const p = attached.get(v);
        if (p) p.stop();
      });
      return;
    }
    streaming = true;
    delayMs = Math.max(0, Number(m.delayMs != null ? m.delayMs : m.totalLatencyMs) || 0);
    const mode = m.realTimeMode ? "RT" : "Normal";
    hud(`BC ${mode} +${Math.round(delayMs)}ms`);
    scan();
  }

  function scan() {
    if (!streaming || delayMs < 15) return;
    document.querySelectorAll("video").forEach((v) => {
      if (v.offsetWidth < MIN_VIDEO_PX || v.offsetHeight < MIN_VIDEO_PX) return;
      if (!v.parentElement) return;
      let p = attached.get(v);
      if (!p) {
        p = new DelayPipe(v);
        attached.set(v, p);
        p.setDelay(delayMs);
        p.start();
      } else {
        p.setDelay(delayMs);
        if (!p.on) p.start();
        if (p.needsLayout()) p._layout();
      }
    });
  }

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type === "beamcast-metrics") apply(msg.metrics);
  });

  setInterval(() => {
    chrome.runtime.sendMessage({ type: "get-metrics" }, (res) => {
      if (chrome.runtime.lastError) return;
      apply(res && res.metrics);
    });
    if (streaming) {
      scan();
      placeHud();
    }
  }, 2000);
})();
