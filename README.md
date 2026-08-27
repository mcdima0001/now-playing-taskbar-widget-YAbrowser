# Now Playing - Taskbar Widget

A now-playing widget embedded right into the Windows taskbar: album art, title
and artist of whatever's playing, with full controls — play/pause, skips and a
seekable progress bar. It follows any player through the Windows media session
(Spotify, YouTube in a browser, Apple Music, Windows Media Player, and more),
and for Spotify it adds the liked state, all three shuffle modes, repeat and
volume. Runs on Windows 10 and Windows 11.

> Formerly "Taskbar Widget for Spotify". Independent project, **not affiliated
> with, sponsored or endorsed by Spotify AB**. "Spotify" is a trademark of
> Spotify AB.

![demo](docs/demo.gif)

## Install

- **Microsoft Store (recommended — always the latest version):**
  [**Now Playing - Taskbar Widget**](https://apps.microsoft.com/detail/9p12tljzg2cj) —
  one-click install, automatic updates, works with Smart App Control enabled,
  and supports both Windows 10 (2004+) and Windows 11.
- **winget** (pending review): `winget install MechanicWB.TaskbarWidgetForSpotify`
- UI languages: English and Portuguese (follows your Windows language).

> The Microsoft Store has the current release. The GitHub Releases here may lag
> behind — if you want the newest version (universal player support, Windows 10),
> install from the Store.

## How it works

- Track data comes from the **Windows media session API (SMTC)** — the same one
  behind the Windows volume flyout. Any player that reports to Windows shows up
  there (Spotify, YouTube, Apple Music, Windows Media Player…), so the widget
  follows whatever is actually playing, with **no login or API keys**.
- For Spotify's extra controls (liked state, Smart Shuffle, repeat, internal
  volume) there is no clean API, so the widget reads Spotify's own accessibility
  tree via UI Automation.
- The taskbar no longer supports "deskbands", so the widget is a borderless
  always-visible window docked over the empty area of the taskbar.
- **Automatic positioning:** it aligns itself next to the clock/weather area and
  never overlaps the app buttons or the system tray. The Windows 10 and 11
  taskbars are completely different internally (11 is XAML, 10 is classic child
  windows), so each has its own positioning logic. Works on any resolution/DPI,
  adapts to left-aligned taskbars, follows auto-hide, and supports multiple
  monitors.
- When the taskbar gets crowded it shrinks to just the album art instead of
  overlapping your app buttons, and hides if there is really no room.
- Hides automatically when an app is fullscreen (games, videos).

## Usage

- **Position:** locked and automatic by default. To move it: right-click →
  *Move widget*, drag, and untick to lock it in the new spot. *Reset to
  automatic position* brings back auto alignment.
- **Choose the player:** right-click → *Player* to follow any player
  automatically (default), lock the widget to Spotify only, or pin the player
  that's currently playing, so YouTube and others don't take it over when you
  only want Spotify.
- **Multiple monitors:** right-click → *Monitor* and tick every taskbar you
  want a widget on — each display gets its own, with shared settings. (Needs
  Windows' "show my taskbar on all displays" enabled for secondary monitors.)
- **Size:** right-click → *Size* → Small / Normal / Large.
- **Brightness:** right-click → *Brightness* — a slider from 20% to 100%, handy
  to dim the widget on OLED or transparent taskbars. Scroll the mouse wheel
  over it to fine-tune.
- **Buttons:** right-click → *Buttons* to choose which controls appear —
  play/pause, favorites (+), shuffle, previous, next, repeat, volume. Hide
  play/pause to use it as a pure now-playing display.
- **Long titles:** scroll continuously by default; right-click → *Scroll title
  only once* to have them scroll once at the start of each track and then rest.
- **Favorites (+, Spotify):** reads and clicks Spotify's own button through the
  Spotify window's accessibility tree — shows a **green check** when the track
  is already saved, and adds it without stealing focus. Spotify freezes this
  info while its window is minimized (and has locked the Web API alternative),
  so the widget shows the green check only when it can actually confirm it,
  otherwise it stays neutral rather than guessing. See
  [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for the full explanation.
- **Shuffle (Spotify):** all three modes — off (gray), shuffle (green) and
  **Smart Shuffle** (green with a star). Repeat supports off / playlist / track.
- **Volume:** for Spotify it moves Spotify's own slider; for other players it
  uses the Windows app volume.
- **Progress bar:** live position at the bottom of the widget; click to seek.
- Settings and error log live in `%APPDATA%\SpotifyTaskbarWidget\`.

## Pro (optional)

The widget is free. An optional paid **Pro** add-on on the Microsoft Store adds
themes, an audio visualizer, synced lyrics and global hotkeys. The whole core
(now-playing, controls, positioning, any-player support, Windows 10 and 11)
stays free.

## Troubleshooting

Something not working? See **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** — covers
"nothing playing", antivirus flags, Smart App Control, positioning and more.
Still stuck?
[Open an issue](https://github.com/mechanicwb2-hub/now-playing-taskbar-widget/issues).

## Support

Free and open source. If you find it useful, you can support development on
**[Ko-fi](https://ko-fi.com/mechanicwb2)** ☕

## Building

Requires the .NET 8 SDK:

```
dotnet publish SpotifyTaskbarWidget.csproj -c Release -o publish
```

Produces a single `SpotifyTaskbarWidget.exe` (needs the .NET 8 Desktop Runtime;
for a standalone exe, flip `SelfContained` to `true`).

> Note: the latest release ships on the Microsoft Store; the source on this
> branch may be behind the Store version.

## Structure

| File | Role |
|---|---|
| `MainWindow.xaml(.cs)` | Widget UI, positioning, responsive layout, menu |
| `MediaService.cs` | Windows media session (track, art, play/pause, timeline) |
| `SpotifyUiaService.cs` | Spotify window accessibility: favorites, shuffle, repeat, volume |
| `SpotifyVolume.cs` | CoreAudio: app volume / play state via the Windows mixer |
| `Interop.cs` | Win32 (taskbar position, topmost, fullscreen detection, input) |
| `WidgetSettings.cs` | Position, scale, theme and visible buttons, stored as JSON |
