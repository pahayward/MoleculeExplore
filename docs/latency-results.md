# Latency results (localhost)

**Setup:** server and Chrome on the same Windows machine, RTX 5060 Ti, driver 32.0.16.1088,
Unity 6000.6.7f1, `com.unity.webrtc` 3.0.0, Direct3D11, GPU texture path, NVENC (`encoderImplementation = NvCodec`).
1280x720 at 60 fps, 5 Mbps cap, host ICE candidates only (no STUN). Client was the Chrome tab inside the
Claude desktop browser pane, driven by synthetic pointer events from a script (not a human).

One 20 s run: 10 s dragging on empty space (camera orbit), then 10 s grabbing the molecule. About 8 ms between events.

| Metric | n | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| Input round trip (send → server ack), ms | 1197 | 17.3 | 23.6 | 29.4 | 30.6 | 35.6 | 24.0 |
| Photon latency (send → block seen in decoded frame), ms | 1117 | 49.5 | 70.2 | 90.3 | 92.0 | 93.3 | 73.7 |

Stream during the run: 1280x720, 60 fps decoded, 4.7 Mbps, jitter 8 ms, 0 packets lost,
decode 0.72 ms/frame, server encode 2.5 ms/frame, server frame time 16.7 ms.

## Reading the numbers

- **Round trip** covers client send, data channel, server applying input on its next frame, and the ack
  back. The floor is set by the 60 Hz server frame (0–16.7 ms wait) plus the data-channel path.
- **Photon latency** adds render, NVENC encode, RTP, the browser jitter buffer, decode, and the point at
  which the browser reports the frame (`requestVideoFrameCallback`). About 45 ms more than the round trip.
  The block is drawn into the same frame the camera renders, so it shows exactly the input state of that frame.
- Only the input whose seq was actually applied is counted. Older moves dropped by the unreliable channel are discarded.
- A first RTT measurement of about 160 ms was wrong: acknowledgements rode on the 4 Hz telemetry message.
  Sending a small ack every frame fixed that (see README).

## Caveats

- Synthetic events, one machine, no real network. LAN and Wi-Fi runs are still to do.
- The photon figure includes whatever the host browser adds before it reports a frame; real Chrome may differ a little.
- The client timestamps are `performance.now()` on one clock, so only deltas are meaningful, as intended.
- Frame display on a real monitor adds up to one more display refresh that this measurement does not see.
- Only one keyframe was produced per session (no keyframe interval control in the package). Not visible on localhost with no loss.
