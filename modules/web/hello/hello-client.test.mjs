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

// 记录页面渲染出去的文本:凭证若被渲染进页面,也是一种对外暴露。
function stubElement(rendered) {
  let text = "";
  const el = {
    rows: { length: 0 },
    replaceChildren() {},
    append(...parts) {
      for (const part of parts) {
        if (typeof part === "string") rendered.push(part);
      }
    },
    prepend() {},
    remove() {},
    deleteRow() {},
  };
  Object.defineProperty(el, "textContent", {
    get: () => text,
    set(value) {
      text = String(value);
      rendered.push(text);
    },
    enumerable: true,
  });
  return el;
}

// 装一个刚好够 hello-client.js 跑起来的浏览器外壳,并接管 fetch / WebSocket 以便观测。
async function runPage({
  search,
  pathname,
  launchResponse = LAUNCH_RESPONSE,
  launchOk = true,
  // 预期页面停在 waiting(既不建连也不记错)时,没有可等的信号,用短超时避免空等。
  settleTimeoutMs = 2000,
}) {
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

  const rendered = [];
  const logged = [];
  const record = (...args) => logged.push(args.map((arg) => String(arg)).join(" "));

  const win = { location: { search, pathname }, __lumioResult: null };
  const sandbox = {
    window: win,
    document: {
      getElementById: () => stubElement(rendered),
      createElement: () => stubElement(rendered),
    },
    WebSocket: ObservableSocket,
    URLSearchParams,
    TextEncoder,
    crypto: webcrypto,
    console: { error: record, warn: record, log: record },
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
  await settle(
    () => sockets.length > 0 || (win.__lumioResult?.errors?.length ?? 0) > 0,
    settleTimeoutMs,
  );

  return { win, fetchCalls, sockets, rendered, logged, result: win.__lumioResult };
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

// 凭证允许出现的地方只有握手帧。把「除此之外页面能把值送出去的所有出口」收集起来统一扫描,
// 逐个列举 sink(而不是只看 __lumioResult)才挡得住「换个地方泄漏」的退化。
// 上游 platform-port-v1.json 的 launch.semantics 点名了 URL 与日志,故日志也在扫描面内。
function collectExposures({ win, fetchCalls, sockets, rendered, logged }) {
  const exposures = [
    ["window", serialize(win)], // 含 __lumioResult 与页面挂到 window 上的任何其他属性
  ];
  for (const call of fetchCalls) exposures.push([`fetch:${call.url}`, call.url]);
  for (const socket of sockets) {
    exposures.push(["ws:url", String(socket.url)]);
    exposures.push(["ws:subprotocol", String(socket.subprotocol ?? "")]);
    // 握手帧(第 0 帧)是凭证唯一合法的去处,不进扫描面;其余帧都要扫。
    for (const [index, frame] of socket.sent.entries()) {
      if (index > 0) exposures.push([`ws:frame[${index}]`, frame]);
    }
  }
  for (const [index, text] of rendered.entries()) exposures.push([`dom[${index}]`, text]);
  for (const [index, line] of logged.entries()) exposures.push([`console[${index}]`, line]);
  return exposures;
}

const findSecret = (exposures, secret) =>
  exposures.filter(([, text]) => text.includes(secret)).map(([label]) => label);

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
  const page = await runPage({
    search: "",
    pathname: "/games/hello-world/",
  });
  const { sockets, result } = page;

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

  // 反向:除握手帧外,凭证不得出现在任何对外可见的出口。
  const exposures = collectExposures(page);
  assert.deepEqual(
    findSecret(exposures, credential),
    [],
    "凭证泄漏到了握手帧之外的出口",
  );
  // 单独点名卡面字面要求的两处,回归时报错信息更直白。
  assert.ok(!serialize(result).includes(credential), "凭证不得进 window.__lumioResult");
  assert.ok(!socket.url.includes(credential), "凭证不得进 WebSocket URL");
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

test("launch 应答缺必需字段时按 launch_failed 处理,不建连", async () => {
  const { sockets, result } = await runPage({
    search: "",
    pathname: "/games/hello-world/",
    launchResponse: { subprotocol: "lumio-launch-v1" }, // 缺 wsUrl 与 admissionCredential
  });

  assert.equal(sockets.length, 0, "应答不完整就不建连");
  assert.equal(result.status, "error");
  assert.deepEqual(
    plain(result.errors).map((error) => error.code),
    ["launch_failed"],
  );
});

test("slug 取 /games/ 之后那一段,而非路径最后一段", async () => {
  const { fetchCalls } = await runPage({
    search: "",
    pathname: "/games/hello-world/index.html",
  });

  assert.deepEqual(
    launchCalls(fetchCalls).map((call) => call.url),
    ["/api/games/hello-world/launch"],
    "深链到具体页面时 slug 仍应是 hello-world",
  );
});

test("路径里没有 /games/ 锚点时停在 waiting:不请求 launch、不建连、不报错", async () => {
  const { fetchCalls, sockets, result } = await runPage({
    search: "",
    pathname: "/some/other/page/",
    settleTimeoutMs: 200,
  });

  assert.deepEqual(launchCalls(fetchCalls), [], "推导不出 slug 就不该请求 launch");
  assert.equal(sockets.length, 0, "推导不出 slug 就不该建连");
  assert.equal(result.status, "running", "这是 waiting 态,不是错误态");
  assert.deepEqual(plain(result.errors), []);
});

// 反向 fixture:把泄漏植入**真实跑出来的 exposure 集合**再扫一遍,证明上面那条全绿不是因为
// 扫描面是空的或 findSecret 恒返回空。真正的抗退化证据是变异测试(见本卡交付记录),
// 这条守的是「扫描器本身别退化成永真式」。
test("凭证扫描器对植入的泄漏必须报警(反向 fixture)", async () => {
  const page = await runPage({ search: "", pathname: "/games/hello-world/" });
  await playFullSession(page.sockets[0]);
  const credential = LAUNCH_RESPONSE.admissionCredential;

  const exposures = collectExposures(page);
  assert.ok(exposures.length > 0, "exposure 集合不能是空的,否则扫描无意义");
  assert.deepEqual(findSecret(exposures, credential), []);

  for (const planted of ["window", "ws:frame[1]", "dom[0]", "console[0]"]) {
    assert.deepEqual(
      findSecret([...exposures, [planted, `prefix-${credential}-suffix`]], credential),
      [planted],
      `扫描器漏掉了植入 ${planted} 的泄漏`,
    );
  }
});
