# Troubleshooting

## The widget says "nothing playing" while Spotify is playing

The track info (title, artist, art, play state) comes from the **Windows media
session** — the same system behind the Windows volume popup. If it's empty:

1. **Update Spotify.** Old versions of the desktop app didn't publish to the
   Windows media session at all. Install the latest from spotify.com or the
   Microsoft Store.
2. **Enable Spotify's media-key overlay.** In Spotify: **Settings → Display →
   turn on "Show desktop overlay when using media keys."** On some versions
   Spotify only publishes the current track to the Windows media session when
   this is on, so with it off the widget shows nothing. (Thanks to the user who
   tracked this down.)
3. **Check the music is playing on this PC** — if you're casting to a phone or
   speaker via Spotify Connect, there's no local session to read.
4. **Check Windows itself:** play a song and press a volume key. If the Windows
   volume popup does *not* show the track name either, your Windows is missing
   media integration — common on Windows 11 **"N" editions** (sold in Europe).
   Fix: Settings → Apps → Optional features → *Add a feature* → install
   **Media Feature Pack**, then reboot.

## Antivirus / VirusTotal flags the installer

See [issue #4](https://github.com/mechanicwb2-hub/now-playing-taskbar-widget/issues/4):
a couple of minor engines heuristically flag unsigned single-file .NET
installers. All major engines report clean, the source is public, and you can
install the Microsoft-signed build from the
[Microsoft Store](https://apps.microsoft.com/detail/9p12tljzg2cj) instead.

## Smart App Control blocks the installer

Windows 11's Smart App Control blocks unsigned apps with no override. Install
from the [Microsoft Store](https://apps.microsoft.com/detail/9p12tljzg2cj) —
that build is signed and certified by Microsoft.

## The widget overlaps something / sits in the wrong place

- Right-click → **Reset to automatic position** (also resets the monitor choice).
- To place it manually: right-click → **Move widget**, drag, untick to lock.
- Found a layout the auto-positioning handles badly? Please
  [open an issue](https://github.com/mechanicwb2-hub/now-playing-taskbar-widget/issues)
  with a screenshot of your full taskbar — that's how the tricky setups get fixed.

## Favorites / Smart Shuffle / repeat state not showing

These come from the Spotify desktop app itself (the widget reads its interface).
Make sure the Spotify **desktop app** is installed and running — the web player
in a browser doesn't expose them. A Spotify update can occasionally break this
integration; if it happens, update the widget (fixes ship quickly) or open an
issue.

## The favorites (♥) checkmark doesn't always turn green

This is a genuine limitation, and it's on Spotify's side — here's the honest
explanation. There is **no public way** to reliably know whether the current
track is saved:

- **Spotify's Web API** used to allow it, but Spotify locked those endpoints
  behind an approval that's out of reach for small apps (2026 changes).
- So the widget reads the saved state from the Spotify desktop app's own
  interface. But **Spotify freezes that information while its window is
  minimized** (to save resources), so the widget can't read it reliably when
  Spotify is in the background.

Because of that, the widget follows a simple rule: **it only shows the green
checkmark when it can actually confirm the track is saved.** When it can't
(Spotify minimized), it stays neutral (a plain +) instead of guessing — it will
never show you a wrong state. The checkmark shows reliably when Spotify has been
open recently. Everything else (play/pause, track info, art, volume, progress)
works regardless. Adding to favorites with the **+** always works.
