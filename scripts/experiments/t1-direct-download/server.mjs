import assert from 'node:assert/strict';
import { fork } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';

// Lab only: synthetic bytes, loopback signaling, no product credentials or authorization.
const bytes = Number(process.argv[2] ?? 100 * 1024 ** 2);
assert(Number.isSafeInteger(bytes) && bytes > 0 && bytes <= 8 * 1024 ** 3);
const corrupt = process.argv.includes('--corrupt');
const remote = process.argv.includes('--remote');
const stun = process.env.T1_LAB_STUN;
assert(!stun || /^stun:[a-zA-Z0-9.-]+:[0-9]{1,5}$/.test(stun), 'Only a STUN endpoint is allowed; never TURN.');
const iceServers = stun ? [stun] : [];
const port = Number(process.env.T1_LAB_PORT ?? 18761);
const origin = `http://127.0.0.1:${port}`;
const assets = new Map([
  ['/', ['browser.html', 'text/html']],
  ['/browser.mjs', ['browser.mjs', 'text/javascript']],
  ['/sha256.js', ['node_modules/hash-wasm/dist/sha256.umd.min.js', 'text/javascript']],
]);
const signals = [];
const metrics = { signalingRequestBytes: 0, signalingResponseBytes: 0, offers: 0, native: null };
let worker;
let consumed = false;
let remoteOffer;
let remoteCancelled = false;

const acceptSignal = message => {
  assert(['description', 'candidate', 'failure', 'metrics'].includes(message.kind));
  if (message.kind === 'metrics') metrics.native = message;
  else {
    assert(signals.length < 32);
    signals.push(message);
  }
};
const readControl = async req => {
  let body = '', size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    metrics.signalingRequestBytes += chunk.length;
    if (size > 32768) throw Object.assign(new Error('SignalingTooLarge'), { status: 413 });
    body += chunk.toString('utf8');
  }
  return JSON.parse(body);
};

const server = createServer(async (req, res) => {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'");
  const reply = (status, value) => {
    const body = JSON.stringify(value);
    metrics.signalingResponseBytes += Buffer.byteLength(body);
    res.writeHead(status, { 'Content-Type': 'application/json' }).end(body);
  };
  try {
    if (req.headers.host !== `127.0.0.1:${port}` ||
        (req.headers.origin && req.headers.origin !== origin)) return reply(403, { error: 'ForeignOrigin' });
    if (req.method === 'GET' && assets.has(req.url)) {
      const [path, type] = assets.get(req.url);
      res.writeHead(200, { 'Content-Type': type }).end(await readFile(new URL(path, import.meta.url)));
    } else if (req.method === 'POST' && req.url === '/offer') {
      if (consumed) return reply(409, { error: 'OfferAlreadyConsumed' });
      consumed = true;
      const offer = await readControl(req);
      assert(offer.type === 'offer' && typeof offer.sdp === 'string');
      metrics.offers++;
      if (remote) remoteOffer = { ...offer, length: bytes, corrupt, iceServers };
      else {
        worker = fork(fileURLToPath(new URL('native.mjs', import.meta.url)), [String(bytes), ...(corrupt ? ['--corrupt'] : [])], { stdio: ['ignore', 'inherit', 'inherit', 'ipc'] });
        worker.on('message', acceptSignal);
        worker.on('exit', code => { if (code) signals.push({ kind: 'failure', error: 'NativeProcessFailed' }); });
        worker.send(offer);
      }
      reply(202, { bytes });
    } else if (req.method === 'POST' && req.url === '/cancel') {
      remoteCancelled = true;
      if (worker?.connected) worker.send({ kind: 'cancel' });
      reply(202, { cancelled: true });
    } else if (remote && req.method === 'GET' && req.url === '/agent-offer') {
      const offer = remoteOffer ?? null;
      remoteOffer = null;
      reply(200, offer);
    } else if (remote && req.method === 'GET' && req.url === '/agent-state') reply(200, { cancelled: remoteCancelled });
    else if (remote && req.method === 'POST' && req.url === '/agent-events') {
      acceptSignal(await readControl(req));
      reply(202, { accepted: true });
    } else if (req.method === 'GET' && req.url === '/signals') reply(200, signals.splice(0));
    else if (req.method === 'GET' && req.url === '/metrics') reply(200, metrics);
    else if (req.method === 'GET' && req.url === '/config') reply(200, { iceServers: iceServers.map(urls => ({ urls })) });
    else reply(404, { error: 'NoPayloadRoute' });
  } catch (error) {
    worker?.kill();
    reply(error.status ?? 400, { error: error.status === 413 ? 'SignalingTooLarge' : 'InvalidExperimentRequest' });
  }
});
server.requestTimeout = 10000;
server.headersTimeout = 10000;
server.listen(port, '127.0.0.1', () => console.log(`T1_LAB_READY ${origin} bytes=${bytes} corrupt=${corrupt} remote=${remote}`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  worker?.kill();
  server.close(() => process.exit(0));
});
