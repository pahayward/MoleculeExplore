'use strict';
// Caffeine remote-render POC client. Chrome only. Server is the offerer.
const params = new URLSearchParams(location.search);
const host = params.get('host') || (location.hostname || 'localhost');
const port = params.get('port') || (location.port || '8080');
const wsUrl = `ws://${host}:${port}/`;

const video = document.getElementById('v');
const statusEl = document.getElementById('status');
const statsEl = document.getElementById('stats');

let pc, ws, inputCh, controlCh;
let seq = 0;
const pending = new Map();          // seq -> t sent (performance.now), for ack round trip
const pendingPh = new Map();        // same, for photon-level latency
const phLat = [];                   // recent photon latencies (ms)
const phLog = [];                   // CSV rows
const rtts = [];                    // recent round-trip samples (ms)
const rttLog = [];                  // CSV rows for each acked input
const statLog = [];                 // CSV rows per second
let lastTelemetry = null;
let lastStats = null;
let remoteSet = false;
const pendingCand = [];

function setStatus(s) { statusEl.textContent = s; }

// ------------------------------------------------------------------ signalling
function connect() {
  ws = new WebSocket(wsUrl);
  ws.onopen = () => setStatus('signalling connected, waiting for offer…');
  ws.onclose = () => setStatus('signalling closed (peer may still be up)');
  ws.onerror = () => setStatus('signalling error: is the server running on ' + wsUrl + ' ?');
  ws.onmessage = async (ev) => {
    const m = JSON.parse(ev.data);
    if (m.type === 'offer') await onOffer(m);
    else if (m.type === 'candidate') {
      const c = { candidate: m.candidate, sdpMid: m.sdpMid, sdpMLineIndex: m.sdpMLineIndex };
      if (remoteSet) pc.addIceCandidate(c).catch(console.warn); else pendingCand.push(c);
    }
  };
}

async function onOffer(m) {
  pc = new RTCPeerConnection({ iceServers: params.get('stun') ? [{ urls: params.get('stun') }] : [] });
  pc.onicecandidate = (e) => {
    if (e.candidate) ws.send(JSON.stringify({ type: 'candidate', candidate: e.candidate.candidate, sdpMid: e.candidate.sdpMid, sdpMLineIndex: e.candidate.sdpMLineIndex }));
  };
  pc.onconnectionstatechange = () => setStatus('peer: ' + pc.connectionState);
  pc.ontrack = (e) => {
    video.srcObject = e.streams[0] || new MediaStream([e.track]);
    try { e.receiver.playoutDelayHint = 0; e.receiver.jitterBufferTarget = 0; } catch (_) {}
  };
  pc.ondatachannel = (e) => {
    const ch = e.channel;
    if (ch.label === 'input') { inputCh = ch; ch.onmessage = onControl; }
    if (ch.label === 'control') { controlCh = ch; ch.onmessage = onControl; }
  };
  await pc.setRemoteDescription({ type: 'offer', sdp: m.sdp });
  remoteSet = true;
  for (const c of pendingCand.splice(0)) pc.addIceCandidate(c).catch(console.warn);
  const ans = await pc.createAnswer();
  await pc.setLocalDescription(ans);
  ws.send(JSON.stringify({ type: 'answer', sdp: ans.sdp }));
}

// ------------------------------------------------------------------ input
// Map a pointer event to 0..1 over the drawn video frame (accounting for object-fit: contain letterboxing).
function norm(e) {
  const r = video.getBoundingClientRect();
  const vw = video.videoWidth || r.width, vh = video.videoHeight || r.height;
  const scale = Math.min(r.width / vw, r.height / vh);
  const dw = vw * scale, dh = vh * scale;
  const ox = r.left + (r.width - dw) / 2, oy = r.top + (r.height - dh) / 2;
  return { x: Math.min(1, Math.max(0, (e.clientX - ox) / dw)), y: Math.min(1, Math.max(0, (e.clientY - oy) / dh)) };
}

function send(type, e, extra) {
  const msg = Object.assign({ seq: ++seq, t: performance.now(), type }, e ? norm(e) : {}, extra || {});
  const reliable = (type !== 'move' && type !== 'wheel');
  const ch = reliable ? controlCh : inputCh;
  if (!ch || ch.readyState !== 'open') return;
  pending.set(msg.seq, msg.t); pendingPh.set(msg.seq, msg.t);
  if (pending.size > 5000) pending.delete(pending.keys().next().value);
  if (pendingPh.size > 5000) pendingPh.delete(pendingPh.keys().next().value);
  ch.send(JSON.stringify(msg));
}

video.addEventListener('pointerdown', (e) => { video.setPointerCapture(e.pointerId); video.focus(); send('down', e, { button: e.button }); e.preventDefault(); });
video.addEventListener('pointermove', (e) => { send('move', e); });
video.addEventListener('pointerup', (e) => { send('up', e, { button: e.button }); });
video.addEventListener('pointercancel', (e) => { send('up', e, { button: e.button }); });
video.addEventListener('wheel', (e) => { send('wheel', e, { deltaY: e.deltaY }); e.preventDefault(); }, { passive: false });
video.addEventListener('contextmenu', (e) => e.preventDefault());
window.addEventListener('keydown', (e) => { if (e.key === 'r' || e.key === 'R') send('key', null, { key: e.key }); });

// ------------------------------------------------------------------ telemetry (server -> client)
function onControl(ev) {
  let m; try { m = JSON.parse(ev.data); } catch { return; }
  if (m.type !== 'telemetry' && m.type !== 'ack') return;
  const now = performance.now();
  if (m.type === 'telemetry') lastTelemetry = m;
  if (m.type === 'ack') { window.__poc.grabbing = m.grabbing; video.style.cursor = m.grabbing ? 'grabbing' : 'grab'; }
  // Record the latency of the input whose seq was actually applied; older seqs (e.g. dropped moves) are discarded.
  const t0 = pending.get(m.lastAppliedSeq);
  if (t0 !== undefined) {
    const rtt = now - t0;
    rtts.push(rtt); if (rtts.length > 200) rtts.shift();
    rttLog.push([new Date().toISOString(), m.lastAppliedSeq, t0.toFixed(3), now.toFixed(3), rtt.toFixed(3)]);
  }
  for (const k of pending.keys()) if (k <= m.lastAppliedSeq) pending.delete(k);
}

// ------------------------------------------------------------------ photon-level latency
// The server draws 16 cells (12 px, bottom-left, 1 cell inset) encoding the last applied seq into every frame.
const CELL = 12, BITS = 16;
const phCanvas = document.createElement('canvas');
phCanvas.width = (BITS + 2) * CELL; phCanvas.height = 3 * CELL;
const phCtx = phCanvas.getContext('2d', { willReadFrequently: true });
let lastPhSeq = -1;
function readBlock() {
  const vw = video.videoWidth, vh = video.videoHeight;
  if (!vw) return -1;
  phCtx.drawImage(video, 0, vh - phCanvas.height, phCanvas.width, phCanvas.height, 0, 0, phCanvas.width, phCanvas.height);
  const y = phCanvas.height - Math.round(1.5 * CELL);
  let v = 0;
  for (let i = 0; i < BITS; i++) {
    const d = phCtx.getImageData((1 + i) * CELL + CELL / 2, y, 1, 1).data;
    v = (v << 1) | (((d[0] + d[1] + d[2]) / 3) > 127 ? 1 : 0);
  }
  return v;
}
function onVideoFrame(now) {
  const v = readBlock();
  if (v >= 0 && v !== lastPhSeq) {
    lastPhSeq = v;
    const t0 = pendingPh.get(v);
    if (t0 !== undefined) {
      const lat = now - t0;
      phLat.push(lat); if (phLat.length > 200) phLat.shift();
      phLog.push([new Date().toISOString(), v, t0.toFixed(3), now.toFixed(3), lat.toFixed(3)]);
    }
    for (const k of pendingPh.keys()) if (k <= v) pendingPh.delete(k);
  }
  video.requestVideoFrameCallback(onVideoFrame);
}
if ('requestVideoFrameCallback' in video) video.requestVideoFrameCallback(onVideoFrame);

function pct(a, p) { if (!a.length) return NaN; const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; }

// ------------------------------------------------------------------ getStats (1 Hz)
setInterval(async () => {
  if (!pc) return;
  const rep = await pc.getStats();
  let inb, cand, codec;
  rep.forEach((r) => {
    if (r.type === 'inbound-rtp' && r.kind === 'video') inb = r;
    if (r.type === 'candidate-pair' && r.nominated && r.state === 'succeeded') cand = r;
    if (r.type === 'codec' && r.mimeType && r.mimeType.startsWith('video')) codec = r;
  });
  if (!inb) return;
  const now = performance.now();
  let bitrate = 0, fps = 0, decMs = 0;
  if (lastStats) {
    const dt = (now - lastStats.t) / 1000;
    bitrate = (inb.bytesReceived - lastStats.bytes) * 8 / dt / 1e6;
    fps = (inb.framesDecoded - lastStats.frames) / dt;
    const df = inb.framesDecoded - lastStats.frames;
    decMs = df > 0 ? (inb.totalDecodeTime - lastStats.dec) * 1000 / df : 0;
  }
  lastStats = { t: now, bytes: inb.bytesReceived, frames: inb.framesDecoded, dec: inb.totalDecodeTime };
  const stunRtt = cand && cand.currentRoundTripTime != null ? cand.currentRoundTripTime * 1000 : NaN;
  const tel = lastTelemetry || {};
  const row = [new Date().toISOString(), now.toFixed(0), inb.frameWidth, inb.frameHeight, fps.toFixed(1), bitrate.toFixed(2),
    inb.jitter != null ? (inb.jitter * 1000).toFixed(2) : '', inb.packetsLost, decMs.toFixed(2), stunRtt.toFixed(2),
    pct(rtts, 0.5).toFixed(2), pct(rtts, 0.95).toFixed(2), pct(phLat, 0.5).toFixed(2), pct(phLat, 0.95).toFixed(2), tel.frameCount ?? '', tel.frameMs ?? '', tel.encodeMs ?? '', tel.encodeFps ?? '', tel.encoder ?? '', tel.lastAppliedSeq ?? ''];
  statLog.push(row);
  statsEl.textContent =
    `codec     ${codec ? codec.mimeType : '?'}  ${tel.encoder || ''}\n` +
    `frame     ${inb.frameWidth}x${inb.frameHeight}  ${fps.toFixed(0)} fps decoded\n` +
    `bitrate   ${bitrate.toFixed(2)} Mbps   jitter ${(inb.jitter * 1000).toFixed(1)} ms   lost ${inb.packetsLost}\n` +
    `decode    ${decMs.toFixed(2)} ms/frame\n` +
    `ICE RTT   ${stunRtt.toFixed(1)} ms\n` +
    `photon    p50 ${pct(phLat, 0.5).toFixed(1)}  p95 ${pct(phLat, 0.95).toFixed(1)} ms  (n=${phLat.length})
` +
    `input RTT p50 ${pct(rtts, 0.5).toFixed(1)}  p95 ${pct(rtts, 0.95).toFixed(1)} ms  (n=${rtts.length})\n` +
    `server    frame ${tel.frameMs ? tel.frameMs.toFixed(1) : '?'} ms  encode ${tel.encodeMs ? tel.encodeMs.toFixed(2) : '?'} ms  seq ${tel.lastAppliedSeq ?? '?'}`;
}, 1000);

// ------------------------------------------------------------------ CSV
function csv(header, rows) { return header.join(',') + '\n' + rows.map((r) => r.join(',')).join('\n') + '\n'; }
function download(name, text) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv' }));
  a.download = name; a.click();
}
document.getElementById('csv').onclick = () => {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  download(`client_stats_${stamp}.csv`, csv(['iso','perf_ms','w','h','fps','mbps','jitter_ms','lost','decode_ms','ice_rtt_ms','input_rtt_p50','input_rtt_p95','photon_p50','photon_p95','srv_frame','srv_frame_ms','srv_encode_ms','srv_encode_fps','srv_encoder','last_applied_seq'], statLog));
  download(`client_photon_${stamp}.csv`, csv(['iso','seq','t_sent_ms','t_displayed_ms','latency_ms'], phLog));
  download(`client_rtt_${stamp}.csv`, csv(['iso','seq','t_sent_ms','t_ack_ms','rtt_ms'], rttLog));
};

window.__poc = { phLat, phLog, rtts, rttLog, statLog, pct };   // for automated checks
connect();
