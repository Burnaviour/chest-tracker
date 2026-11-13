# Burnaviour Chest Tracker

[![Downloads](https://img.shields.io/github/downloads/Burnaviour/chest-tracker/total?label=downloads&color=dac996)](https://github.com/Burnaviour/chest-tracker/releases)
[![Latest release downloads](https://img.shields.io/github/downloads/Burnaviour/chest-tracker/latest/total?label=latest%20release&color=7c3aed)](https://github.com/Burnaviour/chest-tracker/releases/latest)
[![Version](https://img.shields.io/github/v/release/Burnaviour/chest-tracker?label=version&color=a78bfa)](https://github.com/Burnaviour/chest-tracker/releases/latest)
![Views](https://visitor-badge.laobi.icu/badge?page_id=Burnaviour.chest-tracker&left_text=views)

A free Windows app that counts every chest you open in Fortnite and tells you what that means for the
Treasure Hunt: your real odds, the next Treasure Hour, and every treasure you have found.

By [Burnaviour](https://burnaviour.dev). Support-A-Creator code: **BRNYT**.

- Reads the replay files Fortnite saves on your PC. Nothing is uploaded and the app makes no network requests.
- Does not touch the game: no injection, no game memory, no game files changed. It only reads finished replays.
- Windows 10 or 11, 64-bit. Console and cloud players cannot use it, because their replays are not on a PC.

## Download

Get **BurnaviourChestTracker.exe** from the [latest release](https://github.com/Burnaviour/chest-tracker/releases/latest)
and run it. There is nothing to install: one file, about 160 MB because it carries everything it needs.

The file is not code-signed, so Windows may show "Windows protected your PC" the first time. Choose
**More info → Run anyway**. Each release lists the file's SHA-256 so you can check your download.

## What you get

| Tab | What it shows |
| --- | --- |
| **Dashboard** | Chests tracked, last match, this session, chests per match, best match, chests per minute, wins, chests by type, 30-day activity, match history (CSV export). |
| **Treasure Hunt** | Countdown to the next Treasure Hour in your local time. Chests this week and this hunt. Your chance so far for each treasure, from Epic's published odds. How many chests a 50% or 90% chance takes, and where your own pace gets you by the finale. Every treasure found, with date and source. |
| **Stream overlay** | A chest counter for OBS, at `http://localhost:5088/overlay`. |
| **Settings** | Starting chest count, replay folder, start with Windows, on-screen counter, zoom, 18 languages. |

## Set up

1. **Turn on replays in Fortnite.** Settings → Game → Replays → Record Replays → On.
2. **Open Burnaviour Chest Tracker.** It finds your replay folder by itself and counts the matches already saved there
   (Fortnite keeps about the last 100).
3. **Play.** The count goes up a few seconds after each match ends. Fortnite only makes a replay readable once the
   match is over, so the number cannot tick up mid-match.

Closing the window keeps it running in the system tray. Right-click the tray icon to quit.

### Connecting the replay folder

You normally do nothing: the app looks in Fortnite's default folder,

```text
%LOCALAPPDATA%\FortniteGame\Saved\Demos
```

If your replays are somewhere else (another drive, a moved AppData), open **Settings → Replay folder**, paste the
full path of the folder that contains the `.replay` files, and press **Save**. To find it, press `Win + R`, paste the
path above and press Enter; the folder that opens is the one to use.

If the dashboard stays at 0: check that Record Replays is on, play one full match, and wait a few seconds after it ends.

### Your real total

Fortnite deletes old replays, so the first count only covers the matches still saved on this PC. If you know your
real total, enter it in **Settings → Starting chest count**; new chests are added on top. From then on the app keeps
its own history, even after Fortnite deletes a replay.

### Counter over the game (no OBS)

**Settings → Show the counter over the game** puts a small always-on-top counter on screen.

- Fortnite must be in **Windowed Fullscreen** (Settings → Video → Window Mode). Windows cannot draw another window
  over exclusive Fullscreen, and the app will never inject into the game to get around that.
- Drag it where you want, then press `Ctrl+Shift+L` to lock it so clicks pass through to the game.
- `Ctrl+Shift+O` shows or hides it.

### OBS overlay

Add a **Browser Source** with the address `http://localhost:5088/overlay`, width 420, height 120. Keep the tracker
running on the same PC as OBS. The label and the "+ last match" number are set in the **Stream overlay** tab.

## Treasure Hunt numbers

The dates, Treasure Hours and odds in [`ui/treasure.js`](ui/treasure.js) are the ones published by Epic, the same as on
[burnaviour.dev/treasure-hunt](https://burnaviour.dev/treasure-hunt/). Weekly treasure uses the chests you opened this
week; Pirate Shop Access and Gizmos use the whole hunt. Chests opened during a Treasure Hour count with the boosted
odds Epic announced for the first Treasure Hours; later ones may differ.

A chance is a chance: "24% so far" means one player in four with your chest count would have found it by now.

## Build it yourself

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer.

Run from source:

```powershell
dotnet run --project src/ChestCounter -c Release
```

Build one `.exe` with .NET included (nothing else to install except Microsoft WebView2, which Windows 11 already has):

```powershell
.\build.ps1
```

The result is `dist\Burnaviour Chest Tracker.exe`.

Useful options: `--once` (count the saved replays, print the totals, quit), `--data <folder>` (keep the app's data
somewhere other than `%APPDATA%\BurnaviourChestTracker`), `--demos <folder>` (replay folder for this run).

## How it is put together

- `src/ChestCounter/`: the app. Replay analysis, a local server on port 5088 that only answers its own pages, the
  window, the tray icon and the on-screen counter.
- `ui/`: the pages shown in the window (`app.html`, `overlay.html`) and the Treasure Hunt maths (`treasure.js`).
- `lib/`: the open-source Fortnite replay parser.

## Credits and licence

Includes MIT-licensed open-source code; the notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Not affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.
