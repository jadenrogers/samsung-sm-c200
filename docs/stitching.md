# Stitching details

How `gear360 stitch` works, what the quality profiles set, and how they were chosen.

## How it works

The Gear 360 records both lenses side by side in one frame (dual fisheye). ffmpeg's
[`v360`](https://ffmpeg.org/ffmpeg-filters.html#v360) filter (`input=dfisheye:output=e`) maps it to a 2:1
equirectangular frame at the input width. Audio is copied unless `--audio aac` is given.

The output then gets Google's
[Spherical Video V1](https://github.com/google/spatial-media/blob/master/docs/spherical-video-rfc.md) metadata (the XMP
`uuid` box spatial-media writes), injected in-process, so YouTube and 360 players recognise it.

It is a geometric stitch with no seam blending, so a faint seam where the lenses meet is normal. For blended seams see
[stitch-gear360](https://github.com/bilde2910/stitch-gear360) (Hugin-based).

## Lens settings

| Problem | Fix |
| --- | --- |
| Doubled or missing strip at the seam | `--fov` in small steps between about 190 and 200 (default 195) |
| View starts facing the wrong way | `--yaw 90`, `--yaw 180`, ... |
| Horizon tilted | `--pitch`, `--roll` |

Other Gear 360 command lines use 192–193. On the camera this was tested with, 195 lined up best of 185/190/195/200.

## Quality profiles

| Profile | Codec | CRF | Preset | Sampling (`--interp`) | GPU tuning |
| --- | --- | --- | --- | --- | --- |
| Fast | H.264 | 23 | veryfast | bilinear | no |
| Balanced | H.264 | 20 | medium | bilinear | no |
| High | H.264 | 16 | slow | bilinear | yes |
| Max | HEVC | 14 | slow | Lanczos | yes |

Profiles never change the encoder, audio or lens settings. On the command line the profile is applied first, then any
`--codec`, `--crf`, `--preset`, `--interp` or `--hq-tuning` you pass.

### Measurements

10-second 3840x1920 clip (camera HEVC, ~30 Mbit/s), RTX 3080, ffmpeg 6.0. PSNR is against an uncompressed stitch with
the same sampling; above ~45 dB differences are hard to see.

| Profile | NVIDIA NVENC | CPU (libx264 / libx265) |
| --- | --- | --- |
| Fast | 6.2 s, 11.5 Mbit/s, 47.0 dB | 8.5 s, 6.2 Mbit/s, 45.3 dB |
| Balanced | 7.0 s, 13.6 Mbit/s, 48.1 dB | 16.3 s, 15.4 Mbit/s, 47.6 dB |
| High | 9.2 s, 40.2 Mbit/s, 49.5 dB | 25.6 s, 31.9 Mbit/s, 49.3 dB |
| Max | 16.0 s, 37.8 Mbit/s, 48.6 dB* | 117.4 s, 40.0 Mbit/s, 49.5 dB* |

\* Against the sharper Lanczos reference; High scores 47.3 dB against that reference.

- **High** targets YouTube's recommended 35–45 Mbit/s for 4K at 24–30 fps
  ([YouTube upload settings](https://support.google.com/youtube/answer/1722171)). YouTube re-encodes uploads, so more
  rarely helps.
- **Sampling**: bicubic, Lanczos and spline16 cost about 2.5x the `v360` time of bilinear for a slight sharpness gain,
  so only Max uses Lanczos.
- **Fast** barely beats Balanced on a GPU, because the CPU-only `v360` filter sets the pace there.

## GPU encoding

With `--encoder auto` each candidate gets a short test encode, once per run: NVENC, then Quick Sync, then AMF on
Windows and Linux; VideoToolbox on macOS. The first that works is used, otherwise the CPU. If a GPU encode fails on a
file, that file is redone on the CPU. An explicitly chosen encoder that fails its test stops the command before
anything is copied.

Only the encode moves to the GPU; decoding and `v360` stay on the CPU (GPU decoding was slower, because frames must come
back for `v360`). Balanced, 10 s clip, RTX 3080:

| Codec | CPU | NVENC | Speed-up |
| --- | --- | --- | --- |
| H.264 | 15.5 s | 6.2 s | 2.5x |
| HEVC | 26.5 s | 6.3 s | 4.2x |

- **Quality mapping**: NVENC `-cq`, Quick Sync `-global_quality` and AMF QP are CRF + 5 (H.264) or + 7 (HEVC),
  calibrated on NVENC by size and PSNR. VideoToolbox `-q:v` is 100 − 2 × CRF.
- **GPU tuning** (`--hq-tuning`): for NVENC, preset p7 plus `-multipass fullres -spatial_aq 1 -temporal_aq 1
  -rc-lookahead 32 -bf 3 -b_ref_mode middle`. It gets its own test encode, and is dropped (with a log line) on cards
  that reject it. Quick Sync uses `veryslow`, AMF `quality`.
- **hevc_nvenc** gets `-maxrate 400M`; without it 4K output was silently capped at ~20 Mbit/s.
- Quick Sync, AMF and VideoToolbox have not been tested on real hardware. VideoToolbox constant quality may be missing
  on Intel Macs, in which case Auto falls back to the CPU.
