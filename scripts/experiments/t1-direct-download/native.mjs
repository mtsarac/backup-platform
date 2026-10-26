import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import rtc from 'node-datachannel';

const length = Number(process.argv[2]);
const corrupt = process.argv.includes('--corrupt');
const chunkSize = 16384;
const windowSize = 1024 * 1024;
const template = randomBytes(chunkSize);
const hash = createHash('sha256');
const stun = process.env.T1_LAB_STUN;
assert(!stun || /^stun:[a-zA-Z0-9.-]+:[0-9]{1,5}$/.test(stun));
const peer = new rtc.PeerConnection('t1-lab-native', { iceServers: stun ? [stun] : [], maxMessageSize: chunkSize });
let channel, sent = 0, acknowledged = 0, digest, finished = false;
let maxOutstanding = 0, maxBuffered = 0, maxRss = process.memoryUsage().rss;
let lastActivity = Date.now();
const started = Date.now();
const report = (outcome, error) => {
  if (finished) return;
  finished = true;
  process.send({ kind: 'metrics', outcome, error, length, sent, acknowledged, sha256: digest,
    maxOutstanding, maxBuffered, maxRss, durationMs: Date.now() - started,
    pair: peer.getSelectedCandidatePair(), wireBytesSent: peer.bytesSent(), wireBytesReceived: peer.bytesReceived() });
  peer.close();
  clearInterval(watchdog);
  // Allow the IPC report to flush before unloading the native library.
  setTimeout(() => { rtc.cleanup(); process.disconnect(); }, 100);
};
const watchdog = setInterval(() => {
  maxRss = Math.max(maxRss, process.memoryUsage().rss);
  if (Date.now() - lastActivity > 120000) report('Failed', 'DirectPathUnavailableOrReceiverTimeout');
}, 250);
peer.onLocalDescription((sdp, type) => process.send({ kind: 'description', sdp, type }));
peer.onLocalCandidate((candidate, mid) => {
  assert(!candidate.includes(' typ relay '));
  process.send({ kind: 'candidate', candidate, mid });
});
peer.onStateChange(state => {
  if (state === 'failed' || state === 'closed') report('Failed', 'DirectPathUnavailable');
});
const pump = () => {
  while (!finished && sent < length && sent - acknowledged < windowSize) {
    const size = Math.min(chunkSize, length - sent, windowSize - (sent - acknowledged));
    const block = Buffer.from(template.subarray(0, size));
    if (size >= 8) block.writeBigUInt64LE(BigInt(sent));
    hash.update(block);
    if (corrupt && sent + size === length) block[size - 1] ^= 1;
    channel.sendMessageBinary(block);
    sent += size;
    maxOutstanding = Math.max(maxOutstanding, sent - acknowledged);
    maxBuffered = Math.max(maxBuffered, channel.bufferedAmount());
  }
  if (sent === length && !digest) {
    digest = hash.digest('hex');
    channel.sendMessage(JSON.stringify({ kind: 'end', length, sha256: digest }));
  }
};
peer.onDataChannel(dc => {
  assert(!channel && dc.getLabel() === 'artifact');
  channel = dc;
  dc.onMessage(raw => {
    try {
      assert(typeof raw === 'string' && raw.length <= 1024);
      const message = JSON.parse(raw);
      lastActivity = Date.now();
      if (message.kind === 'cancel') return report('Cancelled', 'ReceiverCancelled');
      if (message.kind === 'verified') {
        assert(sent === length && message.length === length && message.sha256 === digest);
        return report('Verified');
      }
      assert(message.kind === 'ack' && Number.isSafeInteger(message.bytes));
      assert(message.bytes >= acknowledged && message.bytes <= sent);
      acknowledged = message.bytes;
      pump();
    } catch {
      report('Failed', 'InvalidReceiverMessageOrHash');
    }
  });
  dc.onError(() => report('Failed', 'ChannelError'));
});
process.on('message', message => {
  if (message.kind === 'cancel') return report('Cancelled', 'ReceiverCancelled');
  try { peer.setRemoteDescription(message.sdp, message.type); }
  catch { report('Failed', 'InvalidOffer'); }
});
process.on('disconnect', () => { if (!finished) { peer.close(); rtc.cleanup(); process.exit(1); } });
