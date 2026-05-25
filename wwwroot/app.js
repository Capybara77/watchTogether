const startScreen = document.getElementById("startScreen");
const roomScreen = document.getElementById("roomScreen");
const startForm = document.getElementById("startForm");
const urlInput = document.getElementById("urlInput");
const stage = document.getElementById("stage");
const stream = document.getElementById("stream");
const emptyState = document.getElementById("emptyState");
const playStreamButton = document.getElementById("playStreamButton");
const statusText = document.getElementById("statusText");
const roleText = document.getElementById("roleText");
const pageMeta = document.getElementById("pageMeta");
const copyLinkButton = document.getElementById("copyLinkButton");
const stopButton = document.getElementById("stopButton");
const hostControls = document.getElementById("hostControls");
const viewerNotice = document.getElementById("viewerNotice");
const backButton = document.getElementById("backButton");
const navigateInput = document.getElementById("navigateInput");
const navigateButton = document.getElementById("navigateButton");

let connection;
let sessionId = getSessionFromPath();
let hostToken = new URLSearchParams(location.search).get("host") || "";
let isController = Boolean(hostToken);
let lastTextInputAt = 0;

startForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  setStartBusy(true);

  try {
    await ensureConnection();
    const result = await connection.invoke("CreateSession", urlInput.value);
    sessionId = result.sessionId;
    hostToken = result.hostToken;
    isController = true;
    history.replaceState(null, "", `/r/${sessionId}?host=${encodeURIComponent(hostToken)}`);
    await joinRoom();
  } catch (error) {
    setStatus(error.message || String(error));
    setStartBusy(false);
  }
});

copyLinkButton.addEventListener("click", async () => {
  const url = `${location.origin}/r/${sessionId}`;

  try {
    await navigator.clipboard.writeText(url);
    setStatus("Ссылка скопирована");
  } catch {
    window.prompt("Ссылка для друга", url);
  }
});

stopButton.addEventListener("click", async () => {
  if (!sessionId || !connection) {
    return;
  }

  await connection.invoke("StopSession", sessionId);
  location.href = "/";
});

backButton.addEventListener("click", async () => {
  if (!sessionId || !isController) {
    return;
  }

  try {
    await connection.invoke("Back", sessionId);
  } catch (error) {
    setStatus(error.message || String(error));
  }
});

navigateButton.addEventListener("click", async () => {
  if (!sessionId || !isController) {
    return;
  }

  try {
    await connection.invoke("Navigate", sessionId, navigateInput.value);
  } catch (error) {
    setStatus(error.message || String(error));
  }
});

navigateInput.addEventListener("keydown", async (event) => {
  if (event.key === "Enter") {
    event.preventDefault();
    navigateButton.click();
  }
});

playStreamButton.addEventListener("click", async (event) => {
  event.stopPropagation();
  await startStreamPlayback();
});

stream.addEventListener("playing", () => {
  emptyState.classList.add("hidden");
  playStreamButton.classList.add("hidden");
});

stream.addEventListener("error", () => {
  emptyState.textContent = "Не удалось открыть видеопоток. Обновите страницу или комнату.";
  emptyState.classList.remove("hidden");
  playStreamButton.classList.remove("hidden");
});

stage.addEventListener("click", async (event) => {
  if (!stream.src || event.target === playStreamButton) {
    return;
  }

  await startStreamPlayback();

  if (!isController) {
    return;
  }

  stage.focus();
  await sendPointerInput("click", event);
});

stage.addEventListener("dblclick", async (event) => {
  if (!isController) {
    return;
  }

  await sendPointerInput("doubleClick", event);
});

stage.addEventListener("wheel", async (event) => {
  if (!isController) {
    return;
  }

  event.preventDefault();
  const point = getNormalizedPoint(event);
  await sendInput({
    type: "wheel",
    x: point.x,
    y: point.y,
    deltaX: event.deltaX,
    deltaY: event.deltaY
  });
}, { passive: false });

stage.addEventListener("beforeinput", async (event) => {
  if (!isController || !event.data) {
    return;
  }

  lastTextInputAt = Date.now();
  await sendInput({ type: "text", text: event.data });
});

stage.addEventListener("keydown", async (event) => {
  if (!isController) {
    return;
  }

  if (event.ctrlKey || event.metaKey || event.altKey) {
    return;
  }

  const isPrintable = event.key.length === 1;
  if (isPrintable && Date.now() - lastTextInputAt < 50) {
    event.preventDefault();
    return;
  }

  event.preventDefault();
  if (isPrintable) {
    await sendInput({ type: "text", text: event.key });
  } else {
    await sendInput({ type: "key", key: event.key });
  }
});

window.addEventListener("DOMContentLoaded", async () => {
  if (sessionId) {
    await ensureConnection();
    await joinRoom();
  }
});

async function ensureConnection() {
  if (connection) {
    return;
  }

  connection = new signalR.HubConnectionBuilder()
    .withUrl("/hub/session")
    .withAutomaticReconnect()
    .build();

  connection.on("status", setStatus);

  connection.on("sessionStopped", () => {
    setStatus("Комната закрыта ведущим");
    isController = false;
    hostControls.classList.add("hidden");
    viewerNotice.classList.remove("hidden");
    stream.removeAttribute("src");
    stream.load();
  });

  connection.on("pageInfo", (info) => {
    pageMeta.textContent = [info.title, info.url].filter(Boolean).join(" - ");
    navigateInput.value = info.url || "";
  });

  connection.on("sessionState", (state) => {
    isController = Boolean(state.controller);
    roleText.textContent = isController ? "Ведущий" : "Зритель";
    navigateInput.value = state.url || "";
    hostControls.classList.toggle("hidden", !isController);
    viewerNotice.classList.toggle("hidden", isController);

    if (state.viewport) {
      stage.style.aspectRatio = `${state.viewport.width} / ${state.viewport.height}`;
    }

    if (state.streamUrl) {
      setStreamUrl(state.streamUrl);
    }
  });

  connection.onreconnecting(() => setStatus("Переподключение..."));
  connection.onreconnected(() => {
    setStatus("Переподключено");
    if (sessionId) {
      joinRoom();
    }
  });

  await connection.start();
}

async function joinRoom() {
  startScreen.classList.add("hidden");
  roomScreen.classList.remove("hidden");
  hostControls.classList.toggle("hidden", !isController);
  viewerNotice.classList.toggle("hidden", isController);
  roleText.textContent = isController ? "Ведущий" : "Зритель";
  setStatus("Подключаемся к комнате...");

  try {
    const result = await connection.invoke("JoinSession", sessionId, hostToken || null);
    isController = Boolean(result.controller);
    hostControls.classList.toggle("hidden", !isController);
    viewerNotice.classList.toggle("hidden", isController);
    roleText.textContent = isController ? "Ведущий" : "Зритель";
    setStatus("Подключено");
  } catch (error) {
    setStatus(error.message || String(error));
  }
}

function setStreamUrl(url) {
  const absoluteUrl = new URL(url, location.origin).toString();
  if (stream.dataset.streamUrl === absoluteUrl) {
    return;
  }

  stream.dataset.streamUrl = absoluteUrl;
  stream.src = absoluteUrl;
  stream.load();
  emptyState.textContent = "Подключаем видеопоток...";
  emptyState.classList.remove("hidden");
  playStreamButton.classList.add("hidden");
  startStreamPlayback();
}

async function startStreamPlayback() {
  try {
    stream.muted = false;
    await stream.play();
    emptyState.classList.add("hidden");
    playStreamButton.classList.add("hidden");
  } catch {
    emptyState.textContent = "Нажмите, чтобы включить видео и звук";
    emptyState.classList.remove("hidden");
    playStreamButton.classList.remove("hidden");
  }
}

async function sendPointerInput(type, event) {
  const point = getNormalizedPoint(event);
  await sendInput({ type, x: point.x, y: point.y });
}

async function sendInput(input) {
  if (!sessionId || !connection || connection.state !== signalR.HubConnectionState.Connected) {
    return;
  }

  try {
    await connection.invoke("SendInput", sessionId, input);
  } catch (error) {
    setStatus(error.message || String(error));
  }
}

function getNormalizedPoint(event) {
  const rect = stream.getBoundingClientRect();
  return {
    x: clamp((event.clientX - rect.left) / rect.width),
    y: clamp((event.clientY - rect.top) / rect.height)
  };
}

function clamp(value) {
  return Math.max(0, Math.min(1, value));
}

function getSessionFromPath() {
  const match = location.pathname.match(/^\/r\/([a-z0-9]+)/i);
  return match ? match[1] : null;
}

function setStatus(message) {
  statusText.textContent = message;
}

function setStartBusy(busy) {
  startForm.querySelector("button").disabled = busy;
}
