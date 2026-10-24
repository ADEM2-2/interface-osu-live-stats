(function () {
  const els = {
    playerName: document.getElementById("player-name"),
    playerRank: document.getElementById("player-rank"),
    lbList: document.getElementById("lb-list"),
    mapId: document.getElementById("map-id"),
    bpm: document.getElementById("bpm"),
    combo: document.getElementById("combo"),
    acc: document.getElementById("acc"),
    ppText: document.getElementById("pp-text"),
    ppFill: document.getElementById("pp-fill"),
    urValue: document.getElementById("ur-value"),
    urFill: document.getElementById("ur-fill"),
    mods: document.getElementById("mods"),
    status: document.getElementById("status"),
  };

  let ws;

  const LB_NAMES = ["placeholder", "placeholder", "placeholder"];

  // osu! stable mod bitmask, in the order the game labels them.
  const MODS = [
    [1 << 0, "NF"], [1 << 1, "EZ"], [1 << 2, "TD"], [1 << 3, "HD"],
    [1 << 4, "HR"], [1 << 5, "SD"], [1 << 6, "DT"], [1 << 7, "RX"],
    [1 << 8, "HT"], [1 << 9, "NC"], [1 << 10, "FL"], [1 << 11, "AT"],
    [1 << 12, "SO"], [1 << 13, "AP"], [1 << 14, "PF"], [1 << 15, "4K"],
    [1 << 16, "5K"], [1 << 17, "6K"], [1 << 18, "7K"], [1 << 19, "8K"],
    [1 << 20, "FI"], [1 << 21, "RN"], [1 << 22, "CN"], [1 << 23, "TP"],
    [1 << 24, "9K"], [1 << 25, "CO"], [1 << 26, "1K"], [1 << 27, "3K"],
    [1 << 28, "2K"], [1 << 29, "V2"], [1 << 30, "MR"],
  ];

  function modsLabel(mods) {
    if (!mods) return "NM";

    const names = [];
    for (const [bit, label] of MODS) {
      if ((mods & bit) === 0) continue;
      // Nightcore already implies DoubleTime, Perfect implies SuddenDeath.
      if (label === "DT" && (mods & (1 << 9)) !== 0) continue;
      if (label === "SD" && (mods & (1 << 14)) !== 0) continue;
      names.push(label);
    }

    return names.length ? names.join("") : "unknown";
  }

  function connect() {
    const protocol = location.protocol === "https:" ? "wss://" : "ws://";
    ws = new WebSocket(protocol + location.host + "/ws");

    ws.onopen = () => setStatus("connected");
    ws.onclose = () => {
      setStatus("reconnecting...");
      setTimeout(connect, 1000);
    };
    ws.onerror = () => ws.close();
    ws.onmessage = (event) => {
      try {
        render(JSON.parse(event.data));
      } catch (e) {
        /* ignore malformed frame */
      }
    };
  }

  function setStatus(text) {
    els.status.textContent = text;
  }

  // Clears live values when no map is being read, so stale numbers never linger
  // on screen after a play ends.
  function resetLive() {
    els.mapId.textContent = "mapID: ----";
    els.bpm.textContent = "BPM: --";
    els.combo.textContent = "0";
    els.acc.textContent = "0.00%";
    els.ppText.textContent = "0pp";
    els.ppFill.style.width = "0%";
    els.urValue.textContent = "--";
    els.urFill.style.width = "0%";
    els.mods.textContent = "mods: --";
    els.playerRank.textContent = "#----";
    els.lbList.innerHTML = "";
  }

  function render(data) {
    if (data.status === "idle") {
      els.playerName.textContent = "waiting";
      setStatus("waiting for osu!");
      resetLive();
      return;
    }

    if (data.status !== "playing") {
      setStatus(data.status === "no_player" ? "not in a map" : "read error");
      resetLive();
      return;
    }

    setStatus("playing");

    els.playerName.textContent = data.playerName || "Player";
    els.mapId.textContent = "mapID: " + (data.mapId || "----");
    els.bpm.textContent = "BPM: " + (data.bpm ? data.bpm.toFixed(1) : "--");
    els.mods.textContent = "mods: " + modsLabel(data.mods);

    els.combo.textContent = data.combo || 0;
    els.acc.textContent = (data.accuracy || 0).toFixed(2) + "%";

    const pp = data.pp || 0;
    const totalPp = data.totalPp || 0;
    els.ppText.textContent = totalPp > 0
      ? Math.round(pp) + " / " + Math.round(totalPp) + "pp"
      : Math.round(pp) + "pp";
    els.ppFill.style.width =
      clamp(totalPp > 0 ? (pp / totalPp) * 100 : (pp / 400) * 100, 0, 100) + "%";

    const ur = data.ur || 0;
    els.urValue.textContent = ur > 0 ? ur.toFixed(1) : "--";
    els.urFill.style.width = clamp((ur / 300) * 100, 0, 100) + "%";

    renderLeaderboard(totalPp);
  }

  function renderLeaderboard(totalPp) {
    const known = totalPp > 0;
    const rank = estimateRank(totalPp);
    els.playerRank.textContent = known ? "#" + rank : "#----";

    if (!known) {
      els.lbList.innerHTML = "";
      return;
    }

    els.lbList.innerHTML = "";
    [rank - 1, rank, rank + 1].forEach((r, i) => {
      const row = document.createElement("li");
      row.className = "lb-row" + (r === rank ? " current" : "");

      const name = r === rank
        ? (els.playerName.textContent || "Player")
        : LB_NAMES[i] || "placeholder";

      row.innerHTML =
        '<span class="rank">#' + r + "</span>" +
        '<span class="name">' + name + "</span>" +
        '<span class="pp">' + (r === rank ? Math.round(totalPp) : "----") + "pp</span>";

      els.lbList.appendChild(row);
    });
  }

  function estimateRank(totalPp) {
    if (!totalPp || totalPp <= 0) return 0;
    return Math.max(1, Math.round(2000000 * Math.pow(totalPp, -1.1)));
  }

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }

  connect();
})();
