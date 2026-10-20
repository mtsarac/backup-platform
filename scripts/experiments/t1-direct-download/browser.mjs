const output = document.querySelector('#result');
const start = document.querySelector('#start');
const save = document.querySelector('#save');
const cancel = document.querySelector('#cancel');
let stop;
window.t1Result = { state: 'Idle' };
const show = value => {
  window.t1Result = value;
  output.textContent = JSON.stringify(value, null, 2);
};

// OPFS exercises real disk writes, but is NOT evidence of a user-visible saved download.
window.runT1 = async ({ blocked = false, cancelAt = 0, delayMs = 0, userFile = false } = {}) => {
  if (start.disabled) throw new Error('ExperimentAlreadyStarted');
  start.disabled = save.disabled = true;
  cancel.disabled = false;
  let pc, dc, stream, root, handle, timer, polling, done = false;
  let received = 0, queued = 0, maxQueued = 0, lastAck = 0;
  let chain = Promise.resolve();
  const began = performance.now();
  const fail = async error => {
    if (done) return;
    done = true;
    clearTimeout(timer);
    clearInterval(polling);
    try { if (dc?.readyState === 'open') dc.send(JSON.stringify({ kind: 'cancel' })); } catch { /* Already closed. */ }
    pc?.close();
    try { await fetch('/cancel', { method: 'POST', keepalive: true }); } catch { /* The native watchdog also bounds shutdown. */ }
    try { await chain; } catch { /* Failure is reported below. */ }
    try { await stream?.abort(); } catch { /* The stream may already be closed. */ }
    let cleanupError;
    try { if (root && handle) await root.removeEntry(handle.name); }
    catch (error) { cleanupError = error.name; }
    show({ state: 'Failed', error: String(error.message ?? error), received, maxQueued,
      cleanupError, durationMs: Math.round(performance.now() - began) });
    cancel.disabled = true;
  };
  stop = () => fail(new Error('Cancelled'));
  cancel.onclick = stop;
  try {
    if (!window.RTCPeerConnection || !window.hashwasm) throw new Error('BrowserUnsupported');
    if (userFile) {
      if (!window.showSaveFilePicker) throw new Error('BrowserDiskStreamingUnsupported');
      handle = await window.showSaveFilePicker({ suggestedName: 't1-synthetic.bin' });
    } else {
      if (!navigator.storage?.getDirectory) throw new Error('BrowserDiskStreamingUnsupported');
      root = await navigator.storage.getDirectory();
      handle = await root.getFileHandle(`t1-${crypto.randomUUID()}.bin`, { create: true });
    }
    stream = await handle.createWritable();
    const hash = await hashwasm.createSHA256();
    const configReply = await fetch('/config');
    if (!configReply.ok) throw new Error('SignalingConfigurationUnavailable');
    const { iceServers } = await configReply.json();
    pc = new RTCPeerConnection({ iceServers: blocked ? [] : iceServers, iceTransportPolicy: blocked ? 'relay' : 'all' });
    dc = pc.createDataChannel('artifact', { ordered: true });
    dc.binaryType = 'arraybuffer';
    timer = setTimeout(() => fail(new Error('DirectPathUnavailableOrTimeout')), blocked ? 10000 : 900000);
    pc.onconnectionstatechange = () => {
      if (['failed', 'closed'].includes(pc.connectionState) && !done) void fail(new Error('DirectPathUnavailable'));
    };
    dc.onerror = () => { if (!done) void fail(new Error('DirectChannelError')); };
    dc.onclose = () => { if (!done) void fail(new Error('DirectChannelClosed')); };
    dc.onmessage = event => {
      if (done) return;
      const size = typeof event.data === 'string' ? 0 : event.data.byteLength;
      queued += size;
      maxQueued = Math.max(maxQueued, queued);
      if (queued > 1024 * 1024) { void fail(new Error('ReceiveWindowExceeded')); return; }
      chain = chain.then(async () => {
        if (done) return;
        if (typeof event.data !== 'string') {
          const bytes = new Uint8Array(event.data);
          if (bytes.length > 16384) throw new Error('OversizedChunk');
          if (delayMs) await new Promise(resolve => setTimeout(resolve, delayMs));
          if (done) return;
          await stream.write(bytes);
          hash.update(bytes);
          received += bytes.length;
          queued -= bytes.length;
          if (cancelAt && received >= cancelAt) { setTimeout(stop, 0); return; }
          if (received - lastAck >= 256 * 1024) {
            lastAck = received;
            dc.send(JSON.stringify({ kind: 'ack', bytes: received }));
          }
          return;
        }
        if (event.data.length > 1024) throw new Error('OversizedCompletion');
        const end = JSON.parse(event.data);
        if (end.kind !== 'end' || received !== end.length || hash.digest() !== end.sha256) throw new Error('ChecksumMismatch');
        await stream.close();
        // Reopen and independently rehash disk data, not just the incoming buffers.
        const diskHash = await hashwasm.createSHA256();
        const file = await handle.getFile();
        for await (const bytes of file.stream()) diskHash.update(bytes);
        if (file.size !== end.length || diskHash.digest() !== end.sha256) throw new Error('DiskChecksumMismatch');
        const stats = await pc.getStats();
        const pairs = [...stats.values()].filter(row => row.type === 'candidate-pair' && row.state === 'succeeded');
        const candidates = [...stats.values()].filter(row => ['local-candidate', 'remote-candidate'].includes(row.type));
        if (!pairs.length || candidates.some(row => row.candidateType === 'relay')) throw new Error('DirectPairNotProven');
        if (root) await root.removeEntry(handle.name);
        dc.send(JSON.stringify({ kind: 'verified', length: file.size, sha256: end.sha256 }));
        done = true;
        clearTimeout(timer);
        clearInterval(polling);
        show({ state: 'Verified', received, sha256: end.sha256, diskRehash: true,
          sink: root ? 'OPFS (not user download)' : 'UserSelectedFile', maxQueued,
          durationMs: Math.round(performance.now() - began),
          pairs, candidates });
        cancel.disabled = true;
        setTimeout(() => pc.close(), 200);
      });
      chain.catch(error => { void fail(error); });
    };
    dc.onopen = () => dc.send(JSON.stringify({ kind: 'ack', bytes: 0 }));
    await pc.setLocalDescription(await pc.createOffer());
    await new Promise((resolve, reject) => {
      if (pc.iceGatheringState === 'complete') return resolve();
      const gatheringTimer = setTimeout(() => reject(new Error('IceGatheringTimeout')), 5000);
      pc.onicegatheringstatechange = () => {
        if (pc.iceGatheringState === 'complete') { clearTimeout(gatheringTimer); resolve(); }
      };
    });
    const response = await fetch('/offer', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pc.localDescription) });
    if (!response.ok) throw new Error(`SignalingRejected:${response.status}`);
    polling = setInterval(async () => {
      if (done) return;
      try {
        const messages = await (await fetch('/signals')).json();
        for (const message of messages) {
          if (message.kind === 'description') await pc.setRemoteDescription({ type: message.type.toLowerCase(), sdp: message.sdp });
          else if (message.kind === 'candidate') await pc.addIceCandidate({ candidate: message.candidate, sdpMid: message.mid });
          else if (message.kind === 'failure') throw new Error(message.error);
        }
      } catch (error) { void fail(error); }
    }, 100);
    show({ state: 'Connecting', blocked, sink: userFile ? 'UserSelectedFile' : 'OPFS' });
  } catch (error) { await fail(error); }
};
start.onclick = () => { void window.runT1(); };
save.onclick = () => { void window.runT1({ userFile: true }); };
window.addEventListener('pagehide', () => { stop?.(); });
