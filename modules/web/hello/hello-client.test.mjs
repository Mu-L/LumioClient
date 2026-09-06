// R-00415: 两种取地址模式与凭证不外泄的机器守护。
//
// 这里不另写一份页面逻辑,而是把真实的 hello-client.js 原样读进来跑:凭证不进 URL、不进
// window.__lumioResult 是红线,守它的测试必须断言真实交付物,不能断言一个副本。
//
// 为什么用 node:vm 而不是 import():hello-client.js 是 .js 且本仓没有 type=module,
// Node 会按 CommonJS 加载它,而 CJS 缓存以解析后的文件名为键、忽略 ?query,同一进程内
// 无法拿到第二个实例。每条用例需要各自的 window / fetch / WebSocket,故给每条用例开一个
// 全新的 vm context,把浏览器全局显式注入——隔离干净,且页面源码一个字节都不用改。
//
// 契约按架构仓 engine/wire/hello-wire-v1.json 的真实形状构造(页面的字段校验由契约驱动,
// 形状对不上就走不到握手之后);本文件不复制协议真值,只取跑通流程所需的最小子集。
//
// 仅用 node: 内置模块,不引入构建与依赖,与 modules/web 既有约束一致;
// 浏览器真机联调仍归 P5-1,本文件只守静态可判的行为。

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";
import { createHash, webcrypto } from "node:crypto";

const HELLO_CLIENT_PATH = new URL("./hello-client.js", import.meta.url);
const HELLO_CLIENT_SOURCE = fs.readFileSync(HELLO_CLIENT_PATH, "utf8");

const CONTRACT_ID = "lumio.hello-wire.v1";

// 形状取自架构仓 hello-wire-v1.json:页面按 messages.*.required 动态核对字段。
const CONTRACT = Object.freeze({
  contractId: CONTRACT_ID,
  roles: ["browser", "bot"],
  transport: { subprotocol: "lumio-hello-v1" },
  sharedTypes: {
    HelloRecord: {
      required: {
        sender: "enum:roles",
        sequence: "u64",
        kind: "const:hello",
        payload: "string",
        payloadSha256: "sha256-hex",
        tickId: "u64",
        revision: "u64",
        originSentAtMs: "epoch-ms",
        committedAtMs: "epoch-ms",
      },
    },
  },
  messages: {
    Handshake: {
      required: {
        messageType: "const:Handshake",
        role: "enum:roles",
        clientName: "string",
        contractId: `const:${CONTRACT_ID}`,
      },
    },
    HandshakeAck: {
      required: {
        messageType: "const:HandshakeAck",
        sessionId: "string",
        role: "enum:roles",
        accepted: "bool",
        contractId: `const:${CONTRACT_ID}`,
      },
    },
    FullSnapshot: {
      required: {
        messageType: "const:FullSnapshot",
        sessionId: "string",
        tickId: "u64",
        revision: "u64",
        helloLog: "array:HelloRecord",
      },
    },
    InputCommand: {
      required: {
        messageType: "const:InputCommand",
        sender: "enum:roles",
        sequence: "u64",
        kind: "const:hello",
        payload: "string",
        payloadSha256: "sha256-hex",
        sentAtMs: "epoch-ms",
      },
    },
    Delta: {
      required: {
        messageType: "const:Delta",
        tickId: "u64",
        revision: "u64",
        sender: "enum:roles",
        sequence: "u64",
        kind: "const:hello",
        payload: "string",
        payloadSha256: "sha256-hex",
        originSentAtMs: "epoch-ms",
        committedAtMs: "epoch-ms",
        commandSequence: "u64",
      },
    },
    Error: { required: { messageType: "const:Error", code: "string", detail: "string" } },
  },
});

const LAUNCH_RESPONSE = Object.freeze({
  wsUrl: "wss://edge.example/play/session-abc",
  subprotocol: "lumio-launch-v1",
  admissionCredential: "test-admission-credential-do-not-leak",
});

const sha256Hex = (text) => createHash("sha256").update(text, "utf8").digest("hex");

function stubElement() {
  // 页面把渲染写进这些 DOM 出口;测试不断言渲染,吞掉即可。
  return {
    textContent: "",
    rows: { length: 0 },
    replaceChildren() {},
    append() {},
    prepend() {},
    remove() {},
    deleteRow() {},
  };
}

// 装一个刚好够 hello-client.js 跑起来的浏览器外壳,并接管 fetch / WebSocket 以便观测。
async function runPage({ search, pathname, launchResponse = LAUNCH_RESPONSE, launchOk = true }) {
  const fetchCalls = [];
  const sockets = [];

  class ObservableSocket {
    constructor(url, subprotocol) {
      this.url = url;
      this.subprotocol = subprotocol;
      this.sent = [];
      sockets.push(this);
    }
    send(data) {
      this.sent.push(data);
    }
    // 把一帧服务端消息交给页面,并等它处理完(页面的 onmessage 是异步的)。
    async deliver(message) {
      this.onmessage({ data: JSON.stringify(message) });
      await drain();
    }
  }

  const win = { location: { search, pathname }, __lumioResult: null };
  const sandbox = {
    window: win,
    document: { getElementById: () => stubElement(), createElement: () => stubElement() },
    WebSocket: ObservableSocket,
    URLSearchParams,
    TextEncoder,
    crypto: webcrypto,
    console: { error() {}, warn() {}, log() {} },
    setTimeout,
    async fetch(url, init) {
      const href = String(url);
      fetchCalls.push({ url: href, method: init?.method ?? "GET", credentials: init?.credentials ?? null });
      if (href.endsWith("contract.json")) {
        return { ok: true, status: 200, json: async () => structuredClone(CONTRACT) };
      }
      if (!launchOk) {
        return { ok: false, status: 401, json: async () => ({}) };
      }
      return { ok: true, status: 200, json: async () => structuredClone(launchResponse) };
    },
  };

  vm.runInNewContext(HELLO_CLIENT_SOURCE, sandbox, { filename: HELLO_CLIENT_PATH.href });

  // 页面主流程是异步的:等它跑到终态(建连成功或记下错误)再断言。
  await settle(() => sockets.length > 0 || (win.__lumioResult?.errors?.length ?? 0) > 0);

  return { win, fetchCalls, sockets, result: win.__lumioResult };
}

async function settle(done, timeoutMs = 2000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (done()) return true;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  return false;
}

const drain = () => new Promise((resolve) => setTimeout(resolve, 5));

// 把会话推到「已收到 Delta」的终态:凭证的去向要在整段会话里都成立,不能只看握手那一瞬。
async function playFullSession(socket) {
  socket.onopen();
  await socket.deliver({
    messageType: "HandshakeAck",
    sessionId: "session-1",
    role: "browser",
    accepted: true,
    contractId: CONTRACT_ID,
  });
  await socket.deliver({
    messageType: "FullSnapshot",
    sessionId: "session-1",
    tickId: 10,
    revision: 10,
    helloLog: [],
  });
  // 页面在这里异步算 payload 摘要后才发出 InputCommand。
  await settle(() => socket.sent.length >= 3);

  const payload = "Hello World";
  await socket.deliver({
    messageType: "Delta",
    tickId: 11,
    revision: 11,
    sender: "bot",
    sequence: 1,
    kind: "hello",
    payload,
    payloadSha256: sha256Hex(payload),
    originSentAtMs: Date.now(),
    committedAtMs: Date.now(),
    commandSequence: 1,
  });
}

const launchCalls = (fetchCalls) => fetchCalls.filter((call) => call.url.includes("/launch"));

// 把任意结构摊平成字符串,用来证明某个值「哪儿都没出现」。
function serialize(value) {
  return JSON.stringify(value, (_key, item) => (item === undefined ? "<undefined>" : item)) ?? "";
}

// 页面在独立 vm context 里跑,它造的数组/对象带的是那个 realm 的原型,deepStrictEqual 会因
// 原型不同而失败。比较前先摊回本 realm 的普通值。
function plain(value) {
  return JSON.parse(JSON.stringify(value));
}

test("无 ?ws= 时经 launch 端口取地址与凭证并据此建连", async () => {
  const { fetchCalls, sockets, result } = await runPage({
    search: "",
    pathname: "/games/hello-world/",
  });

  const launches = launchCalls(fetchCalls);
  assert.equal(launches.length, 1, "应恰好调用一次 launch 端口");
  assert.equal(launches[0].url, "/api/games/hello-world/launch", "slug 应由页面路径推导且同源");
  assert.equal(launches[0].method, "POST");
  assert.equal(launches[0].credentials, "same-origin");

  assert.equal(sockets.length, 1, "应据 launch 应答建立一条 WebSocket");
  assert.equal(sockets[0].url, LAUNCH_RESPONSE.wsUrl, "连接地址取自应答 wsUrl");
  assert.equal(sockets[0].subprotocol, LAUNCH_RESPONSE.subprotocol, "子协议取自应答 subprotocol");
  assert.equal(result.status, "running");
  assert.deepEqual(plain(result.errors), []);
});

test("有 ?ws= 时行为不变:不打 launch 端口,地址取自 query", async () => {
  const harnessUrl = "ws://127.0.0.1:8080/hello";
  const { fetchCalls, sockets, result } = await runPage({
    search: `?ws=${encodeURIComponent(harnessUrl)}`,
    pathname: "/games/hello-world/",
  });

  assert.deepEqual(launchCalls(fetchCalls), [], "考卷本地模式不得调用 launch 端口");
  assert.deepEqual(
    fetchCalls.map((call) => call.url),
    ["./contract.json"],
    "只应 fetch 契约文件",
  );

  assert.equal(sockets.length, 1);
  assert.equal(sockets[0].url, harnessUrl, "地址仍取自 ?ws=");
  assert.equal(sockets[0].subprotocol, CONTRACT.transport.subprotocol, "子协议仍取自契约");
  assert.equal(result.status, "running");
  assert.deepEqual(plain(result.errors), []);
});

test("有 ?ws= 的考卷本地模式全程不携带任何准入凭证", async () => {
  const { sockets, result } = await runPage({
    search: `?ws=${encodeURIComponent("ws://127.0.0.1:8080/hello")}`,
    pathname: "/games/hello-world/",
  });

  await playFullSession(sockets[0]);

  const handshake = JSON.parse(sockets[0].sent[0]);
  assert.equal(handshake.messageType, "Handshake");
  assert.ok(
    !("admissionCredential" in handshake),
    "本地模式没有凭证可带,握手帧不应出现 admissionCredential 字段",
  );
  assert.equal(result.status, "ok");
});

test("凭证只出现在握手帧:不进 URL,不进 window.__lumioResult", async () => {
  const { fetchCalls, sockets, result } = await runPage({
    search: "",
    pathname: "/games/hello-world/",
  });

  const credential = LAUNCH_RESPONSE.admissionCredential;
  const socket = sockets[0];

  // 跑完整段会话(握手 → 基线 → 发指令 → 收 Delta),让凭证有机会在任何一步泄漏出去。
  await playFullSession(socket);
  assert.equal(result.status, "ok", "整段会话应正常走到 ok,否则后面的断言覆盖不到真实路径");
  assert.equal(result.received.length, 1, "应确实收到并记录了 Delta");

  // 正向:凭证确实随握手发出,否则服务端无从放行。
  const handshake = JSON.parse(socket.sent[0]);
  assert.equal(handshake.messageType, "Handshake");
  assert.equal(handshake.admissionCredential, credential, "凭证应在握手帧内");

  // 反向:除握手帧外,凭证不得出现在任何对外可见处。
  assert.ok(!serialize(result).includes(credential), "凭证不得进 window.__lumioResult");
  for (const call of fetchCalls) {
    assert.ok(!call.url.includes(credential), `凭证不得进 fetch URL: ${call.url}`);
  }
  assert.ok(!socket.url.includes(credential), "凭证不得进 WebSocket URL");
  assert.ok(!String(socket.subprotocol ?? "").includes(credential), "凭证不得进 subprotocol");
  for (const [index, frame] of socket.sent.entries()) {
    if (index === 0) continue;
    assert.ok(!frame.includes(credential), `凭证不得出现在握手之后的帧: ${frame}`);
  }
});

test("launch 失败时呈现明确状态且不重试风暴", async () => {
  const { fetchCalls, sockets, result } = await runPage({
    search: "",
    pathname: "/games/hello-world/",
    launchOk: false,
  });

  assert.equal(launchCalls(fetchCalls).length, 1, "失败后不得重试风暴");
  assert.equal(sockets.length, 0, "取不到地址就不建连");
  assert.equal(result.status, "error");
  assert.deepEqual(
    plain(result.errors).map((error) => error.code),
    ["launch_failed"],
  );
});

// 反向 fixture:证明上面的「凭证不得出现」扫描不是空断言——真塞一个凭证进去,它必须抓到。
// 没有这条,凭证扫描可能悄悄退化成永真式而无人察觉。
test("凭证扫描器对真实泄漏必须报警(反向 fixture)", () => {
  const credential = LAUNCH_RESPONSE.admissionCredential;
  const leakedResult = { status: "ok", errors: [], sent: { admissionCredential: credential } };
  assert.ok(serialize(leakedResult).includes(credential), "扫描器漏掉了 __lumioResult 里的泄漏");
  assert.ok(`wss://edge.example/play?cred=${credential}`.includes(credential), "扫描器漏掉了 URL 里的泄漏");
});
