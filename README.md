<p align="center">
  <h1 align="center">AutoRip</h1>
  <p align="center">Automated DVD &amp; Blu-ray ripping with a local web UI.</p>
</p>

---

AutoRip detects optical discs, looks up movie metadata via TMDB, rips to `.mkv` with MakeMKV, transcodes to `.mp4` with HandBrake, then optionally uploads the result to your media server via SFTP. A Blazor Server dashboard shows live progress for every step.

## Features

- **Disc auto-detection** — polls `/dev/sr0`–`/dev/sr3` and shows drive status in real time
- **TMDB integration** — auto-identifies movies by disc label, with manual search fallback
- **Two-phase pipeline** — ripping and transcoding run separately so one drive can rip while another job processes
- **Real-time progress** — SignalR pushes rip %, transcode %, transfer % and speed/ETA to the browser
- **Configurable encoding** — choose a HandBrake preset or set encoder, quality, speed and framerate manually
- **Subtitle extraction** — extract all tracks or preferred languages, with OCR for VobSub (coming soon)
- **Flexible output** — keep files locally, copy to another folder, or upload via SFTP (SSH.NET)
- **Auto-eject &amp; auto-start** — eject disc after rip, or kick off a rip automatically when a disc is inserted
- **Dark-themed UI** — responsive custom CSS, no component libraries
- **Persistent history** — rip jobs, settings and logs stored in SQLite

## How It Works

```
Drive inserted ──▶ TMDB lookup ──▶ [Start Rip]
                                      │
         ┌────────────────────────────┘
         ▼
   ┌──────────┐     ┌──────────────────────────┐
   │ ROP SLOT │────▶│    PROCESSING QUEUE      │
   │ (0 or 1) │     │    (FIFO, sequential)    │
   │          │     │                          │
   │ 1. Rip   │     │ 1. Transcode .mkv → .mp4 │
   │    .mkv  │     │ 2. Extract subtitles     │
   │ 2. Eject │     │ 3. Transfer (SFTP/copy)  │
   └──────────┘     └──────────────────────────┘
```

## Dependencies

| Binary | Package | Required For |
|--------|---------|-------------|
| `dotnet` | .NET SDK 10.0 | Runtime |
| `makemkvcon` | MakeMKV | DVD/Blu-ray → `.mkv` |
| `HandBrakeCLI` | HandBrake CLI | `.mkv` → `.mp4` transcoding |
| `mkvextract` | MKVToolNix | Subtitle extraction |
| `tesseract` | Tesseract OCR | VobSub → `.srt` (planned) |
| `ffmpeg` | FFmpeg | Media processing |
| `blkid` | util-linux | Disc detection |
| `eject` | util-linux | Tray control |
| `udevadm` | systemd | Drive model info |
| `isoinfo` | genisoimage | ISO label reading |
| `volname` | genisoimage | Volume name reading |

## Installation

### Ubuntu / Debian

```bash
git clone https://github.com/your-username/AutoRip.git
cd AutoRip
chmod +x install.sh
./install.sh
```

The script installs .NET 10.0 SDK, all system packages, builds MakeMKV from source, publishes AutoRip to `/opt/autorip`, and creates a `systemd` service.

Start the service:

```bash
sudo systemctl enable --now autorip
```

Then open `http://<device-ip>:5139` in your browser.

### Arch Linux

```bash
# Install dependencies
sudo pacman -S dotnet-sdk mkvtoolnix tesseract ffmpeg cdrtools
yay -S makemkv handbrake-cli

# Build and run
cd AutoRip/AutoRip
dotnet publish -c Release -o /opt/autorip
cd /opt/autorip && dotnet AutoRip.dll
```

### Manual (any Linux)

Ensure all binaries listed above are on `PATH`, then:

```bash
cd AutoRip/AutoRip
dotnet publish -c Release -o ./publish
dotnet ./publish/AutoRip.dll
```

## Configuration

All settings are managed through the web UI at **Settings** and persist to an SQLite database.

| Setting | Default | Description |
|---------|---------|-------------|
| Output directory | `~/Videos/Rips` | Where rip output folders are created |
| HandBrake preset | `Very Fast 1080p30` | Built-in preset, or toggle custom |
| Custom encoder | `x264` | Encoder (`x264`, `x265`, etc.) |
| Quality | `22` | RF/CQ value |
| Speed preset | `veryfast` | Encoder speed preset |
| Auto-delete `.mkv` | On | Delete intermediate `.mkv` after transcode |
| Auto-eject after rip | On | Eject disc when rip finishes |
| Auto-start rip | Off | Start ripping as soon as a disc is detected |
| Max parallel rips | `0` (no limit) | Cap simultaneous rips if you have multiple drives |
| Extract all subtitles | On | Extract every subtitle track |
| Preferred languages | `eng` | Language filter for subtitle extraction |
| OCR VobSub | Off | Run VobSub/SUP through Tesseract |
| TMDB API key | _(none)_ | Enables movie auto-identification |
| SFTP host / port / user / auth | _(none)_ | Remote upload destination |
| Post-transfer mode | `None` | `None`, `SFTP`, `LocalCopy`, or `Both` |

### TMDB API Key

To enable automatic movie identification and poster artwork, register for a free API key at [themoviedb.org](https://www.themoviedb.org/settings/api) and enter it in the Settings page.

## Output Structure

```
~/Videos/Rips/
└── Inception/
    ├── rip/
    │   └── Inception.mkv          ← intermediate (auto-deleted if enabled)
    ├── Inception.mp4              ← final transcoded video
    ├── Inception.eng.srt          ← subtitle tracks
    └── Inception.eng.sdh.srt
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 10.0 |
| Framework | ASP.NET Core (Blazor Server) |
| Real-time | SignalR |
| Database | SQLite (Entity Framework Core) |
| SFTP | SSH.NET |
| CSS | Custom dark theme (no component libraries) |

## License

MIT
