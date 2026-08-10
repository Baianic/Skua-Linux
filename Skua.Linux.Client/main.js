const { app, BrowserWindow, Menu, session, dialog, shell, clipboard } = require('electron');
app.setName('Skua Linux');
const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');
const WebSocket = require('ws');
const net = require('net');
const http = require('http');

const skuaBootStartedAt = process.hrtime.bigint();
function skuaBootMs() {
  return Number(process.hrtime.bigint() - skuaBootStartedAt) / 1_000_000;
}
function skuaBootLog(stage) {
  console.log(`[Skua Boot] +${skuaBootMs().toFixed(1)}ms ${stage}`);
}
skuaBootLog('main process started');

const skuaWindowIconPath = path.join(__dirname, 'Icon.png');

function skuaEnvFlag(name) {
  return /^(1|true|yes|on)$/i.test(String(process.env[name] ?? ''));
}

const skuaPerfTraceEnabled = skuaEnvFlag('SKUA_PERF_TRACE');
const skuaGpuTraceEnabled = skuaEnvFlag('SKUA_GPU_TRACE');
const skuaThemeBaseUrl = 'http://127.0.0.1:8766/__skua_themes__/';

Menu.setApplicationMenu(null);

let gameWin = null;

let skuaStaticServer = null;

const skuaMimeTypes = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.mjs', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.wasm', 'application/wasm'],
  ['.swf', 'application/x-shockwave-flash'],
  ['.png', 'image/png'],
  ['.jpg', 'image/jpeg'],
  ['.jpeg', 'image/jpeg'],
  ['.gif', 'image/gif'],
  ['.svg', 'image/svg+xml'],
  ['.ico', 'image/x-icon']
]);

function startSkuaStaticServer() {
  if (skuaStaticServer) return Promise.resolve();
  const root = path.resolve(__dirname);

  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      let pathname = '/';
      try {
        pathname = decodeURIComponent(new URL(req.url, 'http://127.0.0.1').pathname);
      } catch {
        res.writeHead(400); res.end('Bad request'); return;
      }

      let candidate;
      let noStore = false;

      if (pathname.startsWith('/__skua_themes__/')) {
        const fileName = pathname.slice('/__skua_themes__/'.length);
        if (
          !fileName ||
          fileName.includes('/') ||
          fileName.includes('\\') ||
          path.extname(fileName).toLowerCase() !== '.swf'
        ) {
          res.writeHead(403); res.end('Forbidden'); return;
        }

        const themesRoot = path.join(app.getPath('appData'), 'Skua', 'themes');
        candidate = path.join(themesRoot, fileName);
        noStore = true;
      } else {
        if (pathname === '/') pathname = '/index.html';
        candidate = path.resolve(root, `.${pathname}`);
        if (candidate !== root && !candidate.startsWith(root + path.sep)) {
          res.writeHead(403); res.end('Forbidden'); return;
        }
        noStore =
          candidate.endsWith('index.html') ||
          candidate.endsWith(`${path.sep}skua-parity-ui.js`);
      }

      fs.stat(candidate, (statError, stat) => {
        if (statError || !stat.isFile()) {
          res.writeHead(404); res.end('Not found'); return;
        }
        const contentType = skuaMimeTypes.get(path.extname(candidate).toLowerCase()) || 'application/octet-stream';
        res.writeHead(200, {
          'Content-Type': contentType,
          'Cache-Control': noStore ? 'no-store' : 'public, max-age=3600'
        });
        fs.createReadStream(candidate).pipe(res);
      });
    });

    server.once('error', (error) => {
      if (error?.code === 'EADDRINUSE') {
        console.warn('[Skua HTTP] Port 8766 already in use; using the existing local server.');
        resolve();
        return;
      }
      reject(error);
    });

    server.listen(8766, '127.0.0.1', () => {
      skuaStaticServer = server;
      console.log(`[Skua HTTP] Built-in server: http://127.0.0.1:8766/ (${root})`);
      skuaBootLog('local HTTP server ready');
      resolve();
    });
  });
}

function stopSkuaStaticServer() {
  if (!skuaStaticServer) return;
  try { skuaStaticServer.close(); } catch { /* already closed */ }
  skuaStaticServer = null;
}


let skuaBackendProcess = null;
let skuaBackendRestartTimer = null;
let skuaBackendStopping = false;
let skuaBackendLastStartAt = null;
let skuaBackendLastExit = null;
let skuaBackendLastLaunch = null;
let skuaBackendManualRestartCount = 0;

function findSkuaBackendLaunch() {
  const explicit = process.env.SKUA_BACKEND_EXECUTABLE;
  if (explicit && fs.existsSync(explicit)) {
    return { command: explicit, args: [], cwd: path.dirname(explicit), description: explicit };
  }

  // AppImage/published layout. The final packager can place the self-contained
  // backend under resources/backend without changing this launcher.
  const bundledBinary = path.join(process.resourcesPath, 'backend', 'Skua.Backend.Linux');
  if (fs.existsSync(bundledBinary)) {
    return { command: bundledBinary, args: [], cwd: path.dirname(bundledBinary), description: bundledBinary };
  }

  const bundledDll = path.join(process.resourcesPath, 'backend', 'Skua.Backend.Linux.dll');
  if (fs.existsSync(bundledDll)) {
    return { command: 'dotnet', args: [bundledDll], cwd: path.dirname(bundledDll), description: bundledDll };
  }

  // Development layout used by this Linux port.
  const projectRoot = path.resolve(__dirname, '..');
  const backendProject = path.join(projectRoot, 'Skua.Backend.Linux', 'Skua.Backend.Linux.csproj');
  const debugBinary = path.join(projectRoot, 'Skua.Backend.Linux', 'bin', 'Debug', 'net10.0', 'Skua.Backend.Linux');
  const debugDll = path.join(projectRoot, 'Skua.Backend.Linux', 'bin', 'Debug', 'net10.0', 'Skua.Backend.Linux.dll');

  if (fs.existsSync(debugBinary)) {
    return { command: debugBinary, args: [], cwd: projectRoot, description: debugBinary };
  }

  if (fs.existsSync(debugDll)) {
    return { command: 'dotnet', args: [debugDll], cwd: projectRoot, description: debugDll };
  }

  if (fs.existsSync(backendProject)) {
    return {
      command: 'dotnet',
      args: ['run', '--no-build', '--configuration', 'Debug', '--project', backendProject],
      cwd: projectRoot,
      description: backendProject
    };
  }

  return null;
}

function scheduleSkuaBackendRestart() {
  if (skuaBackendStopping || app.isQuitting) return;
  if (skuaBackendRestartTimer) return;
  skuaBackendRestartTimer = setTimeout(() => {
    skuaBackendRestartTimer = null;
    startSkuaBackend();
  }, 1200);
}

function startSkuaBackend() {
  if (process.env.SKUA_BACKEND_AUTOSTART === '0' || process.env.SKUA_BACKEND_AUTOSTART === 'false') {
    console.log('[Skua Backend] Autostart disabled by SKUA_BACKEND_AUTOSTART.');
    return null;
  }
  if (skuaBackendProcess && skuaBackendProcess.exitCode === null) return skuaBackendProcess;

  const launch = findSkuaBackendLaunch();
  if (!launch) {
    console.error('[Skua Backend] Backend executable/project not found. Build Skua.Backend.Linux first.');
    return null;
  }

  console.log(`[Skua Backend] Starting together with Electron: ${launch.description}`);
  skuaBootLog('spawning backend');
  skuaBackendStopping = false;
  skuaBackendLastStartAt = new Date().toISOString();
  skuaBackendLastLaunch = launch.description;

  const backendEnv = {
    ...process.env,
    SKUA_ELECTRON_OWNED: '1',
    SKUA_THEME_BASE_URL: skuaThemeBaseUrl
  };

  // AppImages are mounted read-only under /tmp/.mount_*.
  // The backend mirrors repository scripts under SKUA_PROJECT_ROOT, so a
  // packaged build must redirect that mutable state to the user's config
  // directory instead of trying to write beside the bundled executable.
  if (app.isPackaged && !backendEnv.SKUA_PROJECT_ROOT) {
    const writableRuntimeRoot = path.join(
      app.getPath('appData'),
      'Skua',
      'LinuxRuntime'
    );
    fs.mkdirSync(writableRuntimeRoot, { recursive: true });
    backendEnv.SKUA_PROJECT_ROOT = writableRuntimeRoot;
    backendEnv.SKUA_APPIMAGE_PACKAGED = '1';
    console.log(`[Skua Backend] Packaged writable runtime: ${writableRuntimeRoot}`);
  }

  const child = spawn(launch.command, launch.args, {
    cwd: launch.cwd,
    env: backendEnv,
    stdio: ['ignore', 'pipe', 'pipe']
  });
  skuaBackendProcess = child;

  child.stdout?.on('data', (chunk) => process.stdout.write(`[Skua Backend] ${chunk}`));
  child.stderr?.on('data', (chunk) => process.stderr.write(`[Skua Backend] ${chunk}`));
  child.on('error', (error) => {
    console.error('[Skua Backend] Failed to start:', error);
    if (skuaBackendProcess === child) skuaBackendProcess = null;
    scheduleSkuaBackendRestart();
  });
  child.on('exit', (code, signal) => {
    console.log(`[Skua Backend] Exit code=${code} signal=${signal ?? '<none>'}`);
    skuaBackendLastExit = {
      code,
      signal: signal ?? null,
      at: new Date().toISOString()
    };
    if (skuaBackendProcess === child) skuaBackendProcess = null;
    if (!skuaBackendStopping) scheduleSkuaBackendRestart();
  });

  return child;
}

function getSkuaBackendProcessStatus() {
  const child = skuaBackendProcess;
  return {
    owned: Boolean(child),
    running: Boolean(child && child.exitCode === null && child.signalCode === null),
    pid: child?.pid ?? null,
    hostConnected: isSkuaSocketOpen(skuaHostSocket),
    rendererConnected: isSkuaSocketOpen(skuaRendererSocket),
    stopping: skuaBackendStopping,
    restartScheduled: Boolean(skuaBackendRestartTimer),
    manualRestartCount: skuaBackendManualRestartCount,
    lastStartAt: skuaBackendLastStartAt,
    lastLaunch: skuaBackendLastLaunch,
    lastExit: skuaBackendLastExit
  };
}

function waitForSkuaBackendExit(child, timeoutMs) {
  if (!child || child.exitCode !== null || child.signalCode !== null) {
    return Promise.resolve(true);
  }

  return new Promise((resolve) => {
    let settled = false;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      child.off('exit', onExit);
      resolve(value);
    };
    const onExit = () => finish(true);
    const timer = setTimeout(() => finish(false), timeoutMs);
    child.once('exit', onExit);
  });
}

async function restartSkuaBackend() {
  if (skuaBackendRestartTimer) {
    clearTimeout(skuaBackendRestartTimer);
    skuaBackendRestartTimer = null;
  }

  skuaBackendManualRestartCount++;
  const previous = skuaBackendProcess;
  const previousPid = previous?.pid ?? null;

  // Do not kill or replace a host that Electron does not own. Development
  // users may intentionally attach a manually-started backend.
  if (!previous && isSkuaSocketOpen(skuaHostSocket)) {
    throw new Error('backend-host-not-owned');
  }

  skuaBackendStopping = true;

  console.log(`[Skua Backend] Manual restart requested; pid=${previousPid ?? '<none>'}.`);

  if (previous && previous.exitCode === null && previous.signalCode === null) {
    try { previous.kill('SIGINT'); } catch { /* already gone */ }

    let exited = await waitForSkuaBackendExit(previous, 2500);
    if (!exited && previous.exitCode === null && previous.signalCode === null) {
      console.warn('[Skua Backend] Graceful restart timeout; forcing SIGKILL.');
      try { previous.kill('SIGKILL'); } catch { /* already gone */ }
      exited = await waitForSkuaBackendExit(previous, 1500);
    }

    if (!exited && previous.exitCode === null && previous.signalCode === null) {
      skuaBackendStopping = false;
      throw new Error('backend-restart-kill-timeout');
    }
  }

  if (skuaBackendProcess === previous) skuaBackendProcess = null;

  // Let the old host WebSocket close before the replacement host registers.
  // This avoids a stale close event racing the newly-started backend.
  for (let attempt = 0; attempt < 20 && isSkuaSocketOpen(skuaHostSocket); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }

  skuaBackendStopping = false;
  const replacement = startSkuaBackend();
  if (!replacement) throw new Error('backend-restart-start-failed');

  return {
    restarted: true,
    previousPid,
    pid: replacement.pid ?? null,
    ...getSkuaBackendProcessStatus()
  };
}

function stopSkuaBackend() {
  skuaBackendStopping = true;
  if (skuaBackendRestartTimer) {
    clearTimeout(skuaBackendRestartTimer);
    skuaBackendRestartTimer = null;
  }
  const child = skuaBackendProcess;
  if (!child || child.exitCode !== null) return;
  try { child.kill('SIGINT'); } catch { /* already gone */ }
  const forceTimer = setTimeout(() => {
    if (child.exitCode === null) {
      try { child.kill('SIGKILL'); } catch { /* already gone */ }
    }
  }, 3000);
  forceTimer.unref?.();
}


/* ---------- WebSocket → TCP proxy (open relay on localhost) ---------- */

/*
 * Packet Interceptor Linux adapter.
 *
 * The Windows client creates a second TCP capture proxy and reconnects the
 * game through it. Aquastar already IS the WebSocket -> TCP transport, so a
 * second proxy would only add latency. We capture the exact byte stream here
 * while forwarding the original bytes unchanged.
 */
const skuaInterceptorState = {
  enabled: false,
  isLogging: true,
  nextId: 0,
  packets: [],
  connections: 0,
  currentHost: null,
  currentPort: null,
  filters: new Map([
    ['Combat', true],
    ['User Data', true],
    ['Join', true],
    ['Jump', true],
    ['Movement', true],
    ['Get Map', true],
    ['Quest', true],
    ['Shop', true],
    ['Equip', true],
    ['Drop', true],
    ['Chat', true],
    ['Auras', true],
    ['Skills', true],
    ['Stats', true],
    ['Inventory', true],
    ['Class', true],
    ['Misc', true],
    ['Other', true]
  ])
};

function packetMatchesInterceptorFilter(name, packet) {
  switch (name) {
    case 'Combat':
      return packet.includes('"cmd":"restRequest"') ||
        packet.includes('"cmd":"gar"') ||
        packet.includes('"cmd":"aggroMon"');
    case 'User Data':
      return packet.includes('"cmd":"retrieveUserData"') ||
        packet.includes('"cmd":"retrieveUserDatas"');
    case 'Join':
      return packet.includes('"cmd":"moveToArea"') ||
        packet.includes('"cmd":"tfer"') ||
        packet.includes('"cmd":"house"') ||
        packet.includes("action='joinOK'");
    case 'Jump': return packet.includes('"cmd":"moveToCell"');
    case 'Movement':
      return packet.includes('"cmd":"mv"') || packet.includes('"cmd":"mtcid"');
    case 'Get Map': return packet.includes('"cmd":"getMapItem"');
    case 'Quest':
      return packet.includes('"cmd":"getQuest"') ||
        packet.includes('"cmd":"acceptQuest"') ||
        packet.includes('"cmd":"tryQuestComplete"') ||
        packet.includes('"cmd":"updateQuest"');
    case 'Shop':
      return packet.includes('"cmd":"loadShop"') ||
        packet.includes('"cmd":"buyItem"') ||
        packet.includes('"cmd":"sellItem"');
    case 'Equip': return packet.includes('"cmd":"equipItem"');
    case 'Drop': return packet.includes('"cmd":"getDrop"');
    case 'Chat':
      return packet.includes('"cmd":"message"') || packet.includes('"cmd":"cc"');
    case 'Auras':
      return packet.includes('"cmd":"aura+p"') ||
        packet.includes('"cmd":"aura-p"') ||
        packet.includes('"cmd":"clearAuras"');
    case 'Skills': return packet.includes('"cmd":"sAct"');
    case 'Stats':
      return packet.includes('"cmd":"uotls"') || packet.includes('"cmd":"tempSta"');
    case 'Inventory':
      return packet.includes('"cmd":"loadInventoryBig"') ||
        packet.includes('"cmd":"loadInventory"');
    case 'Class': return packet.includes('"cmd":"updateClass"');
    case 'Misc':
      return packet.includes('"cmd":"crafting"') ||
        packet.includes('"cmd":"setHomeTown"') ||
        packet.includes('"cmd":"afk"') ||
        packet.includes('"cmd":"summonPet"');
    case 'Other': {
      for (const key of skuaInterceptorState.filters.keys()) {
        if (key !== 'Other' && packetMatchesInterceptorFilter(key, packet)) {
          return false;
        }
      }
      return true;
    }
    default: return false;
  }
}

function captureInterceptedPacket(packetBuffer, outbound) {
  if (!skuaInterceptorState.enabled || !skuaInterceptorState.isLogging) return;
  const packet = packetBuffer.toString('utf8');
  if (!packet) return;
  skuaInterceptorState.packets.push({
    id: ++skuaInterceptorState.nextId,
    packet,
    outbound,
    timestamp: Date.now()
  });
}

function consumePacketStream(state, chunk, outbound) {
  state.pending = Buffer.concat([state.pending, chunk]);
  let delimiter;
  while ((delimiter = state.pending.indexOf(0)) !== -1) {
    const packet = state.pending.subarray(0, delimiter);
    state.pending = state.pending.subarray(delimiter + 1);
    captureInterceptedPacket(packet, outbound);
  }
}

const wss = new WebSocket.Server({
  port: 8181,
  host: '127.0.0.1',
  perMessageDeflate: false
});

function socketDataToBuffer(rawData) {
  if (Buffer.isBuffer(rawData)) {
    return rawData;
  }

  if (Array.isArray(rawData)) {
    return Buffer.concat(rawData);
  }

  return Buffer.from(rawData);
}

wss.on('connection', (ws, req) => {
  const url = new URL(
    req.url,
    'http://localhost'
  );

  const host = url.searchParams.get('host');
  const port = Number(
    url.searchParams.get('port')
  );

  if (!host || !port) {
    ws.close(
      1008,
      'missing host or port'
    );

    return;
  }

  console.log(
    `[Socket Proxy] Conectando em ${host}:${port}`
  );

  const target = net.createConnection({
    port,
    host,
    noDelay: true
  });

  const interceptorInboundStream = { pending: Buffer.alloc(0) };
  const interceptorOutboundStream = { pending: Buffer.alloc(0) };
  skuaInterceptorState.connections += 1;
  skuaInterceptorState.currentHost = host;
  skuaInterceptorState.currentPort = port;

  let bankTrace = null;

  function finishBankTrace(reason) {
    if (!bankTrace) {
      return;
    }

    clearTimeout(bankTrace.timeout);

    console.log(
      '[BANK TRACE] Fim do diagnóstico:',
      {
        motivo: reason,
        blocosTcpRecebidos:
        bankTrace.tcpChunks,
        bytesRecebidos:
        bankTrace.totalBytes,
        pacotesCompletos:
        bankTrace.completePackets,
        bytesPendentes:
        bankTrace.pending.length,
        encontrouLoadBank:
        bankTrace.sawLoadBankResponse,
        encontrouErro:
        bankTrace.sawError
      }
    );

    bankTrace = null;
  }

  function inspectIncomingBankData(chunk) {
    if (!bankTrace) {
      return;
    }

    bankTrace.tcpChunks += 1;
    bankTrace.totalBytes += chunk.length;

    bankTrace.pending = Buffer.concat([
      bankTrace.pending,
      chunk
    ]);

    console.log(
      '[BANK TRACE][TCP → WS] Bloco recebido:',
      {
        numero: bankTrace.tcpChunks,
        tamanho: chunk.length,
        acumulado: bankTrace.totalBytes,
        pendente: bankTrace.pending.length
      }
    );

    let delimiterPosition;

    while (
      (
        delimiterPosition =
        bankTrace.pending.indexOf(0)
      ) !== -1
    ) {
      const packet =
      bankTrace.pending.subarray(
        0,
        delimiterPosition
      );

      bankTrace.pending =
      bankTrace.pending.subarray(
        delimiterPosition + 1
      );

      bankTrace.completePackets += 1;

      const text =
      packet.toString('utf8');

      const commandMatch =
      text.match(
        /"cmd"\s*:\s*"([^"]+)"/i
      );

      const command =
      commandMatch?.[1] ??
      '<sem-cmd-json>';

const containsLoadBank =
/loadBank/i.test(text);

const containsBankInfo =
/bankinfo/i.test(text);

const containsError =
/error|warning|failed|failure/i
.test(text);

if (containsLoadBank) {
  bankTrace.sawLoadBankResponse =
  true;
}

if (containsError) {
  bankTrace.sawError = true;
}

let stringPacketHeader =
'<não-string>';

if (text.startsWith('%')) {
  stringPacketHeader =
  text
  .split('%')
  .filter(Boolean)
  .slice(0, 5)
  .join('|');
}

console.log(
  '[BANK TRACE] Pacote completo:',
  {
    numero:
    bankTrace.completePackets,
    tamanho: packet.length,
    comando: command,
    cabecalhoString:
    stringPacketHeader,
    contemLoadBank:
    containsLoadBank,
    contemBankInfo:
    containsBankInfo,
    contemErro:
    containsError
  }
);
    }
  }

  target.on('connect', () => {
    console.log(
      `[Socket Proxy] TCP conectado em ` +
      `${host}:${port}`
    );
  });

  target.on('data', (chunk) => {
    inspectIncomingBankData(chunk);
    consumePacketStream(interceptorInboundStream, chunk, false);

    if (
      ws.readyState === WebSocket.OPEN
    ) {
      /*
       * Continua encaminhando imediatamente
       * exatamente os mesmos bytes.
       */
      ws.send(
        chunk,
        {
          binary: true
        }
      );
    }
  });

  target.on('close', () => {
    finishBankTrace(
      'conexão-tcp-fechada'
    );

    skuaInterceptorState.connections = Math.max(
      0,
      skuaInterceptorState.connections - 1
    );
    if (skuaInterceptorState.connections === 0) {
      skuaInterceptorState.currentHost = null;
      skuaInterceptorState.currentPort = null;
    }

    ws.close();
  });

  target.on('error', (error) => {
    console.error(
      '[Socket Proxy] Erro TCP:',
      error
    );

    finishBankTrace(
      'erro-tcp'
    );

    ws.close(
      1011,
      error.message
    );
  });

  ws.on('message', (rawData) => {
    const data =
    socketDataToBuffer(rawData);

    const text =
    data.toString('utf8');

    consumePacketStream(interceptorOutboundStream, data, true);

    if (/loadBank/i.test(text)) {
      finishBankTrace(
        'nova-solicitação'
      );

      bankTrace = {
        tcpChunks: 0,
        totalBytes: 0,
        completePackets: 0,
        pending: Buffer.alloc(0),
        sawLoadBankResponse: false,
        sawError: false,
        timeout: null
      };

      bankTrace.timeout =
      setTimeout(
        () => {
          finishBankTrace(
            'timeout-5-segundos'
          );
        },
        5000
      );

      console.log(
        '[BANK TRACE][WS → TCP] ' +
        'Solicitação loadBank detectada:',
        {
          tamanho: data.length,
          terminaEmNulo:
          data.length > 0 &&
          data[data.length - 1] === 0
        }
      );
    }

    target.write(data);
  });

  ws.on('close', () => {
    finishBankTrace(
      'websocket-fechado'
    );

    target.destroy();
  });

  ws.on('error', (error) => {
    console.error(
      '[Socket Proxy] Erro WebSocket:',
      error
    );

    finishBankTrace(
      'erro-websocket'
    );

    target.destroy();
  });
});

// ============================================================
// Ponte local entre o Skua C# e o renderer que contém o Ruffle
// ============================================================

const SKUA_BRIDGE_HOST = '127.0.0.1';
const SKUA_BRIDGE_PORT = 8182;

let skuaRendererSocket = null;
let skuaHostSocket = null;
let skuaBridgeConnectionCounter = 0;

function skuaBridgeLife(message) {
  console.log(
    `[Skua Bridge][life ${new Date().toISOString()}] ${message}`
  );
}

const cachedSkuaLifecycleEvents = new Map();

const skuaBridgeServer = new WebSocket.Server({
  host: SKUA_BRIDGE_HOST,
  port: SKUA_BRIDGE_PORT
});

function sendJson(socket, message) {
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    return false;
  }

  socket.send(JSON.stringify(message));
  return true;
}

function isSkuaSocketOpen(socket) {
  return Boolean(
    socket && socket.readyState === WebSocket.OPEN
  );
}

function getSkuaBridgeStatus() {
  return {
    bridgeListening: true,
    rendererConnected:
      isSkuaSocketOpen(skuaRendererSocket),
    hostConnected:
      isSkuaSocketOpen(skuaHostSocket)
  };
}

function notifySkuaRendererStatus() {
  sendJson(skuaRendererSocket, {
    type: 'bridge-status',
    ...getSkuaBridgeStatus()
  });
}

function normalizeShellArgs(message) {
  return Array.isArray(message?.args)
    ? message.args
    : [];
}

async function spawnDetached(command, args, timeoutMs = 2000) {
  return new Promise((resolve, reject) => {
    const child = spawn(
      command,
      args,
      {
        detached: true,
        stdio: 'ignore'
      }
    );

    let settled = false;

    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      callback(value);
    };

    const timeout = setTimeout(
      () => finish(
        reject,
        new Error(`${command}-spawn-timeout`)
      ),
      timeoutMs
    );

    child.once('spawn', () => {
      child.unref();
      finish(resolve, command);
    });

    child.once('error', (error) => {
      finish(reject, error);
    });
  });
}

async function openInVSCode(targetPath) {
  if (!targetPath) {
    throw new Error('path-required');
  }

  const resolved = path.resolve(String(targetPath));

  if (!fs.existsSync(resolved)) {
    throw new Error('path-not-found');
  }

  const candidates = process.platform === 'win32'
    ? [
        { label: 'code.cmd', command: 'code.cmd', args: [] },
        { label: 'code-insiders.cmd', command: 'code-insiders.cmd', args: [] }
      ]
    : [
        { label: 'code', command: 'code', args: [] },
        { label: 'code-insiders', command: 'code-insiders', args: [] },
        { label: 'codium', command: 'codium', args: [] }
      ];

  if (process.platform === 'linux') {
    const home = app.getPath('home');
    const flatpakApps = [
      {
        id: 'com.visualstudio.code',
        label: 'VSCode Flatpak'
      },
      {
        id: 'com.vscodium.codium',
        label: 'VSCodium Flatpak'
      }
    ];

    for (const flatpakApp of flatpakApps) {
      const desktopFiles = [
        path.join(
          home,
          '.local/share/flatpak/exports/share/applications',
          `${flatpakApp.id}.desktop`
        ),
        path.join(
          '/var/lib/flatpak/exports/share/applications',
          `${flatpakApp.id}.desktop`
        )
      ];

      if (desktopFiles.some((file) => fs.existsSync(file))) {
        candidates.push({
          label: flatpakApp.label,
          command: 'flatpak',
          args: ['run', flatpakApp.id]
        });
      }
    }
  }

  const failures = [];

  for (const candidate of candidates) {
    try {
      await spawnDetached(
        candidate.command,
        [...candidate.args, resolved],
        3000
      );

      return {
        opened: true,
        via: candidate.label,
        path: resolved
      };
    } catch (error) {
      failures.push(
        `${candidate.label}: ${error?.message ?? error}`
      );
    }
  }

  /*
   * Não usamos shell.openPath como fallback aqui.
   * Em alguns desktops Linux essa Promise pode ficar
   * pendurada por minutos e fazia a UI esperar 120s.
   */
  throw new Error(
    'VSCode command unavailable. Tried: ' +
    failures.join(' | ')
  );
}

async function handleRendererShellCommand(socket, message) {
  const id = message?.id ?? null;
  const name = message?.name;
  const args = normalizeShellArgs(message);

  try {
    let result;

    switch (name) {
      case 'dialog.open-file': {
        const title = String(args[0] ?? 'Open File');
        const requestedDirectory = args[1]
          ? path.resolve(String(args[1]))
          : process.cwd();
        const extensions = Array.isArray(args[2])
          ? args[2].map((value) => String(value).replace(/^\./, '')).filter(Boolean)
          : [];
        const multiple = Boolean(args[3]);

        if (!fs.existsSync(requestedDirectory)) {
          fs.mkdirSync(requestedDirectory, { recursive: true });
        }

        const dialogOptions = {
          title,
          defaultPath: requestedDirectory,
          properties: multiple ? ['openFile', 'multiSelections'] : ['openFile']
        };

        if (extensions.length > 0) {
          dialogOptions.filters = [{
            name: `${title} Files`,
            extensions
          }];
        }

        const selection = gameWin
          ? await dialog.showOpenDialog(gameWin, dialogOptions)
          : await dialog.showOpenDialog(dialogOptions);

        result = {
          canceled: selection.canceled,
          paths: selection.canceled ? [] : selection.filePaths,
          path: selection.canceled || selection.filePaths.length === 0
            ? null
            : selection.filePaths[0]
        };
        break;
      }

      case 'dialog.save-text': {
        const title = String(args[0] ?? 'Save File');
        const suggestedName = String(args[1] ?? 'skua.txt');
        const content = String(args[2] ?? '');
        const extension = String(args[3] ?? 'txt').replace(/^\./, '');

        const dialogOptions = {
          title,
          defaultPath: path.join(process.cwd(), suggestedName),
          filters: [{ name: `${title} File`, extensions: [extension] }]
        };

        const selection = gameWin
          ? await dialog.showSaveDialog(gameWin, dialogOptions)
          : await dialog.showSaveDialog(dialogOptions);

        if (selection.canceled || !selection.filePath) {
          result = { canceled: true, path: null };
          break;
        }

        fs.writeFileSync(selection.filePath, content, 'utf8');
        result = { canceled: false, path: selection.filePath };
        break;
      }

      case 'clipboard.write-text': {
        const text = String(args[0] ?? '');
        clipboard.writeText(text);
        result = { written: true, length: text.length };
        break;
      }

      case 'shell.read-text': {
        const targetPath = path.resolve(String(args[0] ?? ''));
        if (!fs.existsSync(targetPath) || !fs.statSync(targetPath).isFile()) {
          throw new Error('file-not-found');
        }
        const content = fs.readFileSync(targetPath, 'utf8');
        result = { path: targetPath, content };
        break;
      }

      case 'shell.clear-client-cache': {
        const targetSession =
          gameWin && !gameWin.isDestroyed()
            ? gameWin.webContents.session
            : session.defaultSession;

        let cacheBytesBefore = null;
        let cacheBytesAfter = null;

        try {
          cacheBytesBefore = await targetSession.getCacheSize();
        } catch {
          // Cache size is diagnostic only.
        }

        await targetSession.clearCache();

        try {
          await targetSession.clearStorageData({
            storages: ['cachestorage']
          });
        } catch {
          // Older Electron versions may not expose CacheStorage separately.
        }

        try {
          cacheBytesAfter = await targetSession.getCacheSize();
        } catch {
          // Cache size is diagnostic only.
        }

        console.log(
          '[Skua Cache] Client HTTP/CacheStorage cleared.',
          { cacheBytesBefore, cacheBytesAfter }
        );

        result = {
          cleared: true,
          cacheBytesBefore,
          cacheBytesAfter
        };
        break;
      }

      case 'shell.clear-ruffle-cache-and-reload': {
        if (!gameWin || gameWin.isDestroyed()) {
          throw new Error('game-window-unavailable');
        }

        const targetSession = gameWin.webContents.session;
        let cacheBytesBefore = null;
        let cacheBytesAfter = null;

        try {
          cacheBytesBefore = await targetSession.getCacheSize();
        } catch {
          // Cache size is diagnostic only.
        }

        // Clear only network/cache storage. Intentionally preserve cookies,
        // LocalStorage and IndexedDB so AQW/Ruffle persistent data is not
        // reset by a performance-recovery action.
        await targetSession.clearCache();

        try {
          await targetSession.clearStorageData({
            storages: ['cachestorage']
          });
        } catch {
          // CacheStorage is optional across Electron versions.
        }

        try {
          cacheBytesAfter = await targetSession.getCacheSize();
        } catch {
          // Cache size is diagnostic only.
        }

        console.log(
          '[Skua Cache] Ruffle cache cleared; renderer reload scheduled.',
          { cacheBytesBefore, cacheBytesAfter }
        );

        result = {
          cleared: true,
          reloading: true,
          persistentStoragePreserved: true,
          cacheBytesBefore,
          cacheBytesAfter
        };

        // Give the renderer enough time to receive shell-command-result and
        // update the UI before the page is torn down. reloadIgnoringCache()
        // creates a fresh Ruffle/AVM2/WASM page context while bypassing the
        // Chromium HTTP cache on the reload itself.
        setTimeout(() => {
          if (!gameWin || gameWin.isDestroyed()) return;

          try {
            gameWin.webContents.reloadIgnoringCache();
          } catch (error) {
            console.error(
              '[Skua Cache] Failed to reload Ruffle renderer:',
              error
            );
          }
        }, 600);

        break;
      }

      case 'shell.beep': {
        if (!gameWin || gameWin.isDestroyed()) {
          throw new Error('game-window-unavailable');
        }
        const count = Math.max(1, Math.min(20, Number(args[0] ?? 1)));
        const delay = Math.max(0, Math.min(5000, Number(args[1] ?? 200)));
        await gameWin.webContents.executeJavaScript(`(async () => {
          const count = ${count};
          const delay = ${delay};
          const AudioCtx = window.AudioContext || window.webkitAudioContext;
          if (!AudioCtx) return false;
          const ctx = new AudioCtx();
          try {
            for (let i = 0; i < count; i++) {
              const osc = ctx.createOscillator();
              const gain = ctx.createGain();
              gain.gain.value = 0.08;
              osc.frequency.value = 880;
              osc.connect(gain);
              gain.connect(ctx.destination);
              osc.start();
              osc.stop(ctx.currentTime + 0.08);
              await new Promise((resolve) => setTimeout(resolve, delay));
            }
          } finally {
            await ctx.close();
          }
          return true;
        })()`);
        result = { beeped: true, count, delay };
        break;
      }

      case 'dialog.open-script': {
        const requestedDirectory = args[0]
          ? path.resolve(String(args[0]))
          : process.cwd();

        fs.mkdirSync(requestedDirectory, { recursive: true });

        const dialogOptions = {
          title: 'Load Script',
          defaultPath: requestedDirectory,
          properties: ['openFile'],
          filters: [
            {
              name: 'Skua Scripts',
              extensions: ['cs']
            }
          ]
        };

        const selection = gameWin
          ? await dialog.showOpenDialog(
              gameWin,
              dialogOptions
            )
          : await dialog.showOpenDialog(
              dialogOptions
            );

        result = {
          canceled: selection.canceled,
          path:
            selection.canceled ||
            selection.filePaths.length === 0
              ? null
              : selection.filePaths[0]
        };

        break;
      }

      case 'shell.open-vscode':
        result = await openInVSCode(args[0]);
        break;

      case 'shell.open-path': {
        const targetPath = path.resolve(String(args[0] ?? ''));

        if (!fs.existsSync(targetPath)) {
          throw new Error('path-not-found');
        }

        const openError = await shell.openPath(targetPath);

        if (openError) {
          throw new Error(openError);
        }

        result = { opened: true, path: targetPath };
        break;
      }

      case 'shell.open-external': {
        const url = String(args[0] ?? '');

        if (!/^https?:\/\//i.test(url)) {
          throw new Error('invalid-url');
        }

        await shell.openExternal(url);
        result = { opened: true, url };
        break;
      }

      case 'backend.process.status': {
        result = getSkuaBackendProcessStatus();
        break;
      }

      case 'backend.restart': {
        result = await restartSkuaBackend();
        break;
      }

      case 'interceptor.status': {
        const afterId = Math.max(0, Number(args[0] ?? 0));
        const search = String(args[1] ?? '').trim().toLowerCase();
        const visible = skuaInterceptorState.packets.filter((entry) => {
          if (entry.id <= afterId) return false;
          if (search && !entry.packet.toLowerCase().includes(search)) return false;
          for (const [name, checked] of skuaInterceptorState.filters) {
            if (!checked && packetMatchesInterceptorFilter(name, entry.packet)) return false;
          }
          return true;
        });
        result = {
          enabled: skuaInterceptorState.enabled,
          isLogging: skuaInterceptorState.isLogging,
          running: skuaInterceptorState.enabled && skuaInterceptorState.connections > 0,
          transportConnected: skuaInterceptorState.connections > 0,
          host: skuaInterceptorState.currentHost,
          port: skuaInterceptorState.currentPort,
          lastId: skuaInterceptorState.nextId,
          filters: Array.from(skuaInterceptorState.filters, ([name, isChecked]) => ({ name, isChecked })),
          packets: visible
        };
        break;
      }

      case 'interceptor.enable': {
        skuaInterceptorState.enabled = Boolean(args[0]);
        result = {
          enabled: skuaInterceptorState.enabled,
          running: skuaInterceptorState.enabled && skuaInterceptorState.connections > 0
        };
        break;
      }

      case 'interceptor.logging': {
        skuaInterceptorState.isLogging = Boolean(args[0]);
        result = { isLogging: skuaInterceptorState.isLogging };
        break;
      }

      case 'interceptor.filter': {
        const name = String(args[0] ?? '');
        if (name === '__clear__') {
          for (const key of skuaInterceptorState.filters.keys()) {
            skuaInterceptorState.filters.set(key, false);
          }
        } else if (skuaInterceptorState.filters.has(name)) {
          skuaInterceptorState.filters.set(name, Boolean(args[1]));
        } else {
          throw new Error('interceptor-filter-not-found');
        }
        result = {
          filters: Array.from(skuaInterceptorState.filters, ([filterName, isChecked]) => ({
            name: filterName,
            isChecked
          }))
        };
        break;
      }

      case 'interceptor.clear': {
        skuaInterceptorState.packets.length = 0;
        result = { cleared: true, lastId: skuaInterceptorState.nextId };
        break;
      }

      default:
        throw new Error(`unknown-shell-command: ${name}`);
    }

    sendJson(socket, {
      type: 'shell-command-result',
      id,
      success: true,
      result
    });
  } catch (error) {
    sendJson(socket, {
      type: 'shell-command-result',
      id,
      success: false,
      error: String(error?.message ?? error)
    });
  }
}

skuaBridgeServer.on('listening', () => {
  console.log(
    `[Skua Bridge] Servidor ativo em ` +
    `ws://${SKUA_BRIDGE_HOST}:${SKUA_BRIDGE_PORT}`
  );

  skuaBridgeLife(
    `Servidor 8182 em listening; ` +
    `address=${JSON.stringify(skuaBridgeServer.address())}`
  );
});

skuaBridgeServer.on('error', (error) => {
  skuaBridgeLife(
    `Servidor 8182 error; ` +
    `name=${error?.name ?? '<unknown>'}; ` +
    `message=${JSON.stringify(error?.message ?? String(error))}`
  );
});

skuaBridgeServer.on('connection', (socket, request) => {
  const connectionId = ++skuaBridgeConnectionCounter;

  console.log('[Skua Bridge] Nova conexão');
  skuaBridgeLife(
    `conn=${connectionId} connected; ` +
    `remote=${request?.socket?.remoteAddress ?? '<unknown>'}:` +
    `${request?.socket?.remotePort ?? '<unknown>'}`
  );

  let socketRole = null;

  socket.on('message', (buffer) => {
    let message;

    try {
      message = JSON.parse(buffer.toString());
    } catch (error) {
      console.error(
        '[Skua Bridge] JSON inválido:',
        error
      );

      sendJson(socket, {
        type: 'error',
        error: 'invalid-json'
      });

      return;
    }

    if (message.type === 'hello') {
      socketRole = message.role;

      skuaBridgeLife(
        `conn=${connectionId} hello; ` +
        `role=${socketRole ?? '<null>'}; ` +
        `readyState=${socket.readyState}`
      );

      if (socketRole === 'renderer') {
        skuaRendererSocket = socket;

        console.log(
          '[Skua Bridge] Renderer conectado'
        );

        // Start the owned backend only after the renderer is registered, so
        // the backend's first Flash/ExternalInterface calls cannot race a
        // missing renderer connection. If a manually started backend is
        // already attached, do not create a duplicate host.
        if (!skuaHostSocket || skuaHostSocket.readyState !== WebSocket.OPEN) {
          startSkuaBackend();
        }
      } else if (socketRole === 'host') {
        skuaHostSocket = socket;

        console.log(
          '[Skua Bridge] Host C#/teste conectado'
        );
      } else {
        console.warn(
          '[Skua Bridge] Papel desconhecido:',
          socketRole
        );
      }

      sendJson(socket, {
        type: 'hello-ack',
        role: socketRole,
        ...getSkuaBridgeStatus()
      });

      notifySkuaRendererStatus();

      if (socketRole === 'host') {
        const preLoadEvent =
        cachedSkuaLifecycleEvents.get(
          'pre-load'
        );

        const loadedEvent =
        cachedSkuaLifecycleEvents.get(
          'loaded'
        );

        if (preLoadEvent) {
          console.log(
            '[Skua Bridge] Reenviando evento pre-load'
          );

          sendJson(
            socket,
            preLoadEvent
          );
        }

        if (loadedEvent) {
          console.log(
            '[Skua Bridge] Reenviando evento loaded'
          );

          sendJson(
            socket,
            loadedEvent
          );
        }
      }

      return;
    }

    if (socketRole === 'host') {
      if (!sendJson(skuaRendererSocket, message)) {
        sendJson(socket, {
          type: 'result',
          id: message.id ?? null,
          success: false,
          error: 'renderer-not-connected'
        });
      }

      return;
    }

    if (socketRole === 'renderer') {
      if (message.type === 'shell-command') {
        void handleRendererShellCommand(socket, message);
        return;
      }

      if (
        message.type === 'event' &&
        (
          message.name === 'pre-load' ||
          message.name === 'loaded'
        )
      ) {
        cachedSkuaLifecycleEvents.set(
          message.name,
          message
        );

        console.log(
          '[Skua Bridge] Evento armazenado:',
          message.name
        );
      }

      sendJson(
        skuaHostSocket,
        message
      );

      return;
    }

    sendJson(socket, {
      type: 'error',
      error: 'hello-required'
    });
  });

  socket.on('close', (code, reason) => {
    const reasonText = Buffer.isBuffer(reason)
      ? reason.toString('utf8')
      : String(reason ?? '');

    skuaBridgeLife(
      `conn=${connectionId} close; ` +
      `role=${socketRole ?? '<unregistered>'}; ` +
      `code=${code}; ` +
      `reason=${JSON.stringify(reasonText)}; ` +
      `readyState=${socket.readyState}; ` +
      `wasRenderer=${socket === skuaRendererSocket}; ` +
      `wasHost=${socket === skuaHostSocket}`
    );

    if (socket === skuaRendererSocket) {
      skuaRendererSocket = null;
      console.log('[Skua Bridge] Renderer desconectado');
    }

    if (socket === skuaHostSocket) {
      skuaHostSocket = null;
      console.log('[Skua Bridge] Host desconectado');

      // If the renderer is still alive, recover the backend automatically.
      // This also fixes the old workflow where restarting the backend or an
      // unexpected host disconnect required a separate manual terminal step.
      if (skuaRendererSocket && skuaRendererSocket.readyState === WebSocket.OPEN) {
        if (!skuaBackendProcess || skuaBackendProcess.exitCode !== null) {
          scheduleSkuaBackendRestart();
        }
      }
    }

    notifySkuaRendererStatus();
  });

  socket.on('error', (error) => {
    skuaBridgeLife(
      `conn=${connectionId} socket error; ` +
      `role=${socketRole ?? '<unregistered>'}; ` +
      `name=${error?.name ?? '<unknown>'}; ` +
      `message=${JSON.stringify(error?.message ?? String(error))}; ` +
      `readyState=${socket.readyState}`
    );

    console.error(
      '[Skua Bridge] Erro no socket:',
      error
    );
  });
});

/* ---------- game window ---------- */

function configureAqwCors() {
  const filter = {
    urls: [
      'https://game.aq.com/*',
      'https://*.aq.com/*',
      'https://*.artix.com/*'
    ]
  };

  session.defaultSession.webRequest.onHeadersReceived(
    filter,
    (details, callback) => {
      const headers = { ...details.responseHeaders };

      // Remove possíveis versões anteriores para evitar cabeçalhos duplicados.
      for (const name of Object.keys(headers)) {
        const lowerName = name.toLowerCase();

        if (
          lowerName === 'access-control-allow-origin' ||
          lowerName === 'access-control-allow-methods' ||
          lowerName === 'access-control-allow-headers'
        ) {
          delete headers[name];
        }
      }

      headers['Access-Control-Allow-Origin'] = ['http://127.0.0.1:8766'];
      headers['Access-Control-Allow-Methods'] = [
        'GET, POST, OPTIONS, HEAD'
      ];
      headers['Access-Control-Allow-Headers'] = ['*'];

      callback({
        responseHeaders: headers
      });
    }
  );
}

function sanitizeAqwUrl(rawUrl) {
  try {
    const url = new URL(rawUrl);

    /*
     * Mantém os nomes dos parâmetros,
     * mas não registra tokens ou valores pessoais.
     */
    for (
      const parameterName
      of Array.from(url.searchParams.keys())
    ) {
      url.searchParams.set(
        parameterName,
        '<redacted>'
      );
    }

    return url.toString();
  } catch {
    return '<url-invalida>';
  }
}

function getResponseHeader(
  headers,
  requestedName
) {
  if (!headers) {
    return null;
  }

  const requestedLower =
  requestedName.toLowerCase();

  for (
    const [name, values]
    of Object.entries(headers)
  ) {
    if (
      name.toLowerCase() ===
      requestedLower
    ) {
      return values;
    }
  }

  return null;
}

function configureAqwNetworkTrace() {
  const filter = {
    urls: [
      'https://game.aq.com/*',
      'https://*.aq.com/*',
      'https://*.artix.com/*',
      'http://game.aq.com/*',
      'http://*.aq.com/*',
      'http://*.artix.com/*'
    ]
  };

  session.defaultSession
  .webRequest
  .onBeforeRequest(
    filter,
    (details, callback) => {
      console.log(
        '[AQW HTTP][REQUEST]',
        {
          id: details.id,
          method: details.method,
          resourceType:
          details.resourceType,
          url:
          sanitizeAqwUrl(
            details.url
          )
        }
      );

      callback({});
    }
  );

  session.defaultSession
  .webRequest
  .onCompleted(
    filter,
    (details) => {
      console.log(
        '[AQW HTTP][RESPONSE]',
        {
          id: details.id,
          method: details.method,
          statusCode:
          details.statusCode,
          fromCache:
          details.fromCache,
          resourceType:
          details.resourceType,
          url:
          sanitizeAqwUrl(
            details.url
          ),
          allowOrigin:
          getResponseHeader(
            details.responseHeaders,
            'access-control-allow-origin'
          ),
          allowCredentials:
          getResponseHeader(
            details.responseHeaders,
            'access-control-allow-credentials'
          ),
          contentType:
          getResponseHeader(
            details.responseHeaders,
            'content-type'
          )
        }
      );
    }
  );

  session.defaultSession
  .webRequest
  .onErrorOccurred(
    filter,
    (details) => {
      console.error(
        '[AQW HTTP][ERROR]',
        {
          id: details.id,
          method: details.method,
          error: details.error,
          resourceType:
          details.resourceType,
          url:
          sanitizeAqwUrl(
            details.url
          )
        }
      );
    }
  );

  console.log(
    '[AQW HTTP] Diagnóstico ativado.'
  );
}

function createGameWindow() {
  skuaBootLog('creating BrowserWindow');
  gameWin = new BrowserWindow({
    width: 1280,
    height: 720,
    show: false,
    title: 'Skua Linux',
    icon: fs.existsSync(skuaWindowIconPath) ? skuaWindowIconPath : undefined,
    autoHideMenuBar: true,
    fullscreenable: true,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,

      /*
       * O AQW realiza requisições HTTP a partir
       * do SWF carregado pelo servidor local.
       *
       * O endpoint do banco responde 302 ao
       * preflight OPTIONS, fazendo o Chromium
       * bloquear o POST antes que ele seja enviado.
       */
      webSecurity: false,

      webgl: true,
      backgroundThrottling: false,
      allowRunningInsecureContent: false
    }
  });

  /*
   * Evita que o Chromium reutilize uma versão antiga
   * do documento HTML entre inicializações durante o
   * desenvolvimento. A query afeta apenas index.html;
   * os assets relativos mantêm suas URLs/cache normais.
   */
  const rendererUrl =
    `http://127.0.0.1:8766/index.html?skua_ui=${Date.now()}` +
    `&skua_perf=${skuaPerfTraceEnabled ? '1' : '0'}` +
    `&skua_gpu=${skuaGpuTraceEnabled ? '1' : '0'}`;

  /*
   * Encaminha apenas o diagnóstico agregado do renderer
   * para o stdout do processo principal. Assim o teste
   * pode rodar com o DevTools fechado sem perder as linhas
   * [SKUA PERF] e sem espelhar todo o console do AQW/Ruffle.
   *
   * Electron 38 expõe a mensagem em details.message.
   */
  if (fs.existsSync(skuaWindowIconPath)) {
    try { gameWin.setIcon(skuaWindowIconPath); } catch { /* Linux/Wayland may ignore runtime icon updates */ }
  }

  // Fatal renderer errors are always forwarded: this is near-zero overhead
  // during healthy runs and keeps tester logs actionable. Heavy PERF/GPU
  // diagnostics remain opt-in.
  gameWin.webContents.on(
    'console-message',
    (...args) => {
      const detailCandidate = args[1] ?? args[0];
      const message =
        typeof detailCandidate?.message === 'string'
          ? detailCandidate.message
          : typeof args[2] === 'string'
            ? args[2]
            : typeof args[1] === 'string'
              ? args[1]
              : '';

      if (message.startsWith('[SKUA ERROR]')) {
        console.error(`[Skua Renderer] ${message}`);
        return;
      }

      if (
        (skuaPerfTraceEnabled && message.startsWith('[SKUA PERF]')) ||
        (skuaGpuTraceEnabled && message.startsWith('[SKUA GPU]'))
      ) {
        console.log(message);
      }
    }
  );

  gameWin.loadURL(rendererUrl);
  skuaBootLog('renderer URL requested');

  if (
    process.env.SKUA_DEVTOOLS === '1' ||
    process.env.SKUA_DEVTOOLS === 'true'
  ) {
    gameWin.webContents.openDevTools({ mode: 'detach' });
  }

  // GPU-process crashes can prevent Chromium's ready-to-show event from
  // arriving even though the native BrowserWindow already exists. In that
  // state KDE shows a taskbar entry for Skua but the window stays hidden.
  // Showing from multiple safe signals plus a short fallback keeps the app
  // recoverable while we diagnose the separate NVIDIA/WebGL issue.
  let skuaWindowShown = false;
  const showSkuaWindow = (reason) => {
    if (!gameWin || gameWin.isDestroyed() || skuaWindowShown) return;
    skuaWindowShown = true;
    console.log(`[Skua Window] Showing BrowserWindow (${reason}).`);
    skuaBootLog(`BrowserWindow shown via ${reason}`);
    gameWin.show();
    gameWin.focus();
  };

  gameWin.once('ready-to-show', () => showSkuaWindow('ready-to-show'));
  gameWin.webContents.once('did-finish-load', () => showSkuaWindow('did-finish-load'));
  gameWin.webContents.on('did-fail-load', (_event, code, description, validatedURL, isMainFrame) => {
    if (isMainFrame !== false) {
      console.error(`[Skua Window] did-fail-load code=${code} description=${description} url=${validatedURL}`);
      showSkuaWindow('did-fail-load fallback');
    }
  });
  gameWin.webContents.on('render-process-gone', (_event, details) => {
    console.warn(
      `[Skua Window] renderer-process-gone reason=${details?.reason ?? ''} ` +
      `exitCode=${details?.exitCode ?? ''}`
    );
  });
  gameWin.on('unresponsive', () => {
    console.warn('[Skua Window] BrowserWindow became unresponsive.');
  });
  setTimeout(() => showSkuaWindow('3s safety fallback'), 3000);

  gameWin.on('closed', () => {
    skuaBridgeLife('BrowserWindow closed.');
    gameWin = null;
  });
}

// GPU policy for Linux/NVIDIA.
// After the NVIDIA userspace/kernel modules are in sync, native Wayland is the
// validated path on this machine: Electron keeps GPU compositing + WebGL/2
// enabled and ANGLE reports the real NVIDIA renderer. XWayland, by contrast,
// repeatedly crashed Chromium's GPU process (exit 139) and fell back to
// SwiftShader. Keep an explicit user-provided --ozone-platform untouched.
const skuaWaylandSession =
  process.platform === 'linux' &&
  (process.env.XDG_SESSION_TYPE === 'wayland' || Boolean(process.env.WAYLAND_DISPLAY));
const skuaNvidiaPresent =
  process.platform === 'linux' && fs.existsSync('/proc/driver/nvidia/version');
const skuaHasExplicitOzonePlatform = process.argv.some((arg) =>
  String(arg).startsWith('--ozone-platform=')
);

if (skuaWaylandSession && skuaNvidiaPresent && !skuaHasExplicitOzonePlatform) {
  app.commandLine.appendSwitch('ozone-platform', 'wayland');
  console.log('[Skua GPU] NVIDIA + Wayland detected; using native Wayland presentation.');
}

// Chromium can temporarily block WebGL/3D APIs for a domain after repeated
// GPU-process crashes. For this trusted local app, keep 3D APIs available so
// Ruffle can retry after Chromium selects a working GPU backend.
if (typeof app.disableDomainBlockingFor3DAPIs === 'function') {
  app.disableDomainBlockingFor3DAPIs();
}

app.commandLine.appendSwitch('ignore-gpu-blocklist');
app.commandLine.appendSwitch('disable-renderer-backgrounding');
app.commandLine.appendSwitch('disable-background-timer-throttling');

let skuaGpuInfoLogged = false;
if (skuaGpuTraceEnabled) app.on('gpu-info-update', async () => {
  if (skuaGpuInfoLogged) return;
  skuaGpuInfoLogged = true;

  try {
    if (typeof app.isHardwareAccelerationEnabled === 'function') {
      console.log(
        `[Skua GPU] hardwareAccelerationEnabled=${app.isHardwareAccelerationEnabled()}`
      );
    } else {
      console.log('[Skua GPU] hardwareAccelerationEnabled API unavailable in this Electron build.');
    }
    console.log(
      `[Skua GPU] featureStatus=${JSON.stringify(app.getGPUFeatureStatus())}`
    );

    const gpuInfo = await app.getGPUInfo('basic');
    const active = Array.isArray(gpuInfo?.gpuDevice)
      ? gpuInfo.gpuDevice.find((device) => device?.active) ?? gpuInfo.gpuDevice[0]
      : null;
    if (active) {
      console.log(
        `[Skua GPU] activeDevice vendorId=${active.vendorId ?? ''} ` +
        `deviceId=${active.deviceId ?? ''} driverVendor=${active.driverVendor ?? ''} ` +
        `driverVersion=${active.driverVersion ?? ''}`
      );
    }
  } catch (error) {
    console.warn(`[Skua GPU] Failed to query Electron GPU status: ${error?.message ?? error}`);
  }
});

app.on('child-process-gone', (_event, details) => {
  if (details?.type === 'GPU') {
    console.warn(
      `[Skua GPU] child-process-gone reason=${details.reason ?? ''} ` +
      `exitCode=${details.exitCode ?? ''} serviceName=${details.serviceName ?? ''}`
    );
  }
});

app.whenReady().then(async () => {
  skuaBootLog('Electron app ready');
  await startSkuaStaticServer();
  configureAqwCors();

  if (
    process.env.SKUA_AQW_TRACE === '1' ||
    process.env.SKUA_AQW_TRACE === 'true'
  ) {
    configureAqwNetworkTrace();
  } else {
    console.log(
      '[AQW HTTP] Diagnóstico detalhado desativado; ' +
      'use SKUA_AQW_TRACE=1 para habilitar.'
    );
  }

  createGameWindow();
});
app.on('before-quit', () => {
  app.isQuitting = true;
  stopSkuaBackend();
  stopSkuaStaticServer();
  skuaBridgeLife('Electron before-quit.');
});

app.on('will-quit', () => {
  skuaBridgeLife('Electron will-quit.');
});

app.on('quit', (_event, exitCode) => {
  skuaBridgeLife(`Electron quit; exitCode=${exitCode}.`);
});

app.on('window-all-closed', () => {
  skuaBridgeLife('Electron window-all-closed -> app.quit().');
  app.quit();
});
