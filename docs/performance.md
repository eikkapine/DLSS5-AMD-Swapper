# Performance

`v0.1.0-pre.3-soft-cheat.3` keeps the accepted dev.14 performance layout: native visible output with only the neural branch capped to **480 pixels high**. A 2560×1440 source uses roughly **854×480** for neural work while the bridge stays 2560×1440.

The branch also carries dev.14's shared D3D11/D3D12 transport, inference-aware HIP pacing, direct WGC capture path and high-rate visible submissions. Duplicate visible presents remain available for Lossless Scaling cadence without rewriting correction history.

## Experimental clarity compositor

The GPU path keeps dev.14's asynchronous residual alignment and motion rejection, then changes the retained signal:

1. subtract the current low-resolution neural input from the asynchronous neural output
2. remove broad spatial residual components that are prone to flicker
3. retain more local neural structure and bounded luminance/shading than the balanced main path
4. suppress neural chroma and magnitude as motion rises
5. derive local luminance contrast from the current source frame
6. mildly reduce bright neutral veiling
7. add the bounded result to the untouched native source

The source-derived clarity term is current-frame-only, so it remains stable during motion even when the neural result is asynchronous.

## Cost

The clarity compositor reuses the same four neighboring low-resolution input samples already needed for residual filtering. It adds arithmetic rather than another neural pass or another full-resolution texture traversal. The neural network remains the dominant cost.

Strength starts at `1.0` and can be raised to `4.0`. The current-frame clarity adjustment is clamped so the 4× setting cannot create an unbounded tone shift.

## Measurement boundary

No fresh numeric game-FPS benchmark has been recorded for soft-cheat.3. This update changes compositor motion/history gating rather than the neural resolution or transport/pacing layout, but bridge feed or submit cadence is not a game-FPS measurement.

Lossless Scaling provides this bridge with the final color frame, not engine depth or motion vectors. The clarity/dehaze behavior is therefore image-space processing rather than reconstruction of hidden scene data.

Raw runtime logs, private configuration and vendor binaries stay outside the repository.
