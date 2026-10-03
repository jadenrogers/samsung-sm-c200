# Gear360Extractor

Copy photos and videos off the original **Samsung Gear 360 (SM-C200, 2016)** over USB, and optionally stitch them into
360 MP4s that YouTube and VLC play as 360. Samsung's own desktop software is gone, and the camera only speaks MTP, not
USB storage. This project provides a command line tool (`gear360`) and a desktop app (`Gear360Extractor`) for Windows and
macOS.

Tested on Windows 11 with a real SM-C200. macOS is built in CI but untested with a camera. The 2017 SM-R210 is
untested.

## Features

- **Copy over USB**, or from the microSD card in a card reader.
- **Safe re-runs**: files go to `<destination>/<yyyy-MM-dd>/`. Files already copied are skipped, and partial files are
  never left behind.
- **Filters**: videos only, or files since a date. Optional delete from the camera after a verified copy (off by
  default).
- **360 stitching** to `<name>_stitched.mp4` with 360 metadata. Uses your GPU when it can (2.5–4x faster), with
  Fast/Balanced/High/Max quality profiles. Needs ffmpeg, which the app can download for you.

## Install

Download from [Releases](https://github.com/jadenrogers/samsung-sm-c200/releases). These are single self-contained
executables, with no .NET install needed.

- **Windows**: unzip and run. The builds are unsigned, so if SmartScreen warns, choose **More info → Run anyway**.
- **macOS**: run `brew install libmtp` (needed for USB, not for SD cards), unzip, then clear the quarantine flag:
  `xattr -dr com.apple.quarantine Gear360Extractor.app` (or `gear360`).

### ffmpeg (stitching only)

ffmpeg is not bundled. Use any one of these:

- **Already installed**: found on `PATH` and in common install folders. To install it: `winget install Gyan.FFmpeg`
  or `brew install ffmpeg`.
- **Download on request**: run `gear360 ffmpeg install`, click **Download ffmpeg** in the app, or add `--get-ffmpeg`.
  This fetches a pinned, SHA-256-checked ffmpeg 9.0.2 from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (Windows)
  or [martin-riedl.de](https://ffmpeg.martin-riedl.de/) (macOS/Linux) into the app-data folder. These builds are
  GPL v3 and run as separate programs.
- **Point at it**: use `--ffmpeg <path>`, or **Browse for ffmpeg** in the app.

`gear360 ffmpeg status` shows which ffmpeg will be used and which GPU encoders work.

## Command line

```
gear360 list                                  # what's on the camera
gear360 copy -o D:\Gear360                    # copy everything new
gear360 copy -o D:\Gear360 --videos-only --stitch -q high
gear360 copy --source E:\ -o D:\Gear360       # from the SD card
gear360 stitch D:\Gear360 -q max              # stitch files you already have
```

Run `gear360 <command> --help` for every option. The main ones:

| Option | Commands | Meaning |
| --- | --- | --- |
| `-o, --output <dir>` | copy, stitch | Destination folder. Required for `copy`. |
| `-s, --source <folder>` | list, copy | Read a folder or SD card instead of the USB camera. |
| `-d, --device <name>` | list, copy | Pick a camera when several are connected. |
| `--videos-only`, `--since <date>` | copy | Filter what is copied. |
| `--delete-after` | copy | Delete from the camera after each verified copy. |
| `--stitch` | copy | Also stitch the copied videos. Takes the stitch options. |
| `-q, --quality <profile>` | stitch | `fast`, `balanced` (default), `high` or `max`. |
| `--encoder <name>` | stitch | `auto` (default), `cpu`, `nvidia`, `intel`, `amd` or `apple`. |
| `--codec`, `--crf`, `--preset`, `--interp`, `--hq-tuning` | stitch | Override the profile. |
| `--fov`, `--yaw`, `--pitch`, `--roll` | stitch | Lens alignment and view direction (FOV default 195). |
| `--audio <copy\|aac>` | stitch | Re-encode audio to AAC for picky players. |
| `--overwrite` | stitch | Replace existing stitched files. |

Exit codes: `0` ok · `1` a file failed · `2` no camera or input · `3` ffmpeg or the chosen encoder unavailable ·
`130` cancelled.

## Desktop app

Pick the camera, or **Open folder / SD card**. Tick files, choose a destination, and optionally tick **Stitch videos to
360°** and pick a **Quality**. Under **More stitch settings** you can set the encoder, codec, CRF, lens alignment and
ffmpeg. Editing a setting the profile controls switches the profile to **Custom**. Your settings are remembered between
runs.

## Quality profiles

| Profile | Use it for | NVENC, 10 s of 4K |
| --- | --- | --- |
| Fast | Quick previews | 6 s, ~12 Mbit/s |
| Balanced (default) | Everyday | 7 s, ~14 Mbit/s |
| High | YouTube uploads (they recommend 35–45 Mbit/s) | 9 s, ~40 Mbit/s |
| Max | Archiving (HEVC, sharper sampling) | 16 s, ~38 Mbit/s |

The stitch is geometric, so a faint seam is normal. If the seam doubles or misses a strip, adjust `--fov` between 190
and 200. Details, CPU timings and how the settings were chosen are in [docs/stitching.md](docs/stitching.md).

## Troubleshooting

- **No camera found**: switch the camera on, use a data cable (not charge-only), and charge a flat battery first.
- **Windows**: the camera should show as **Gear 360** under portable devices. Close Photos, import dialogs and
  Explorer windows using it.
- **macOS "could not be opened"**: quit Photos, Image Capture and Android File Transfer, then run `killall ptpcamerad`
  just before `gear360`.
- **macOS "libmtp was not found"**: run `brew install libmtp`, or set `GEAR360_LIBMTP_PATH`.
- **Anything else**: take the microSD card out and use `--source`.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build Gear360Extractor.slnx
dotnet test Gear360Extractor.slnx          # on macOS/Linux skip tests/Gear360.Mtp.Windows.Tests
dotnet run --project src/Gear360.Cli -f net10.0-windows -- list    # use -f net10.0 on macOS/Linux
```

`net10.0-windows` builds use Windows Portable Devices ([MediaDevices](https://github.com/Bassman2/MediaDevices)), and
`net10.0` builds use [libmtp](https://github.com/libmtp/libmtp). The GUI uses [Avalonia](https://avaloniaui.net).
Releases are built by `.github/workflows/release.yml` when a `v*` tag is pushed.

| Path | Contents |
| --- | --- |
| `src/Gear360.Core` | Import, ffmpeg stitcher, spherical metadata injector |
| `src/Gear360.Mtp.Windows`, `src/Gear360.Mtp.LibMtp` | USB backends |
| `src/Gear360.Platform` | Picks the backend for the OS |
| `src/Gear360.Cli`, `src/Gear360.Gui` | `gear360` and `Gear360Extractor` |
| `tests/` | xUnit tests |

## Built with AI

This project was built with AI assistance (Claude Code) and checked against a real SM-C200.

## License

[MIT](LICENSE). Not affiliated with Samsung; Samsung and Gear 360 are trademarks of Samsung Electronics. Keep a copy of
your footage before using `--delete-after`.
