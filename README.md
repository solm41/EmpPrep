# EMP Prep — Cove extension

Turns a Cove selection into an upload-ready package: full-resolution screenshots, dotted tags,
performer and studio names, a BBCode description, and a private `.torrent`.

> Adult content. For personal, authorized use only. You are responsible for what you upload and for
> the rules of the tracker you upload to.

## The core constraint: nothing is duplicated

The torrent is hashed **from the file at its existing library path**. Nothing is copied, moved, or
renamed on disk, so your library keeps exactly one copy of the content and the only new bytes are
the screenshots and the few KB of the `.torrent` itself.

To seed, point your client at the folder the file is already in as the save path. It will recheck
against the existing data, find it complete, and start seeding in place.

For a single video:

| Mode | Torrent shape | Disk cost |
|---|---|---|
| **In place** (default) | Single file, named exactly as the file on disk | Nothing |
| **In the pack folder** (opt-in) | Folder named for the release, holding a hard link to the file | Nothing — a hard link is a second name for the same data |

Several videos become a pack, which is always a hard-linked folder — see [Packs](#packs-several-videos-one-torrent).

The in-place mode deliberately uses the file's own name as the torrent name. A client looks for
`<savepath>/<name>`, so renaming there would break the recheck — the folder mode is for when a
release needs its own name.

## Every torrent is private

`private=1` is written into the info dictionary unconditionally. It is not a setting and not a
checkbox, because the flag is part of the infohash: noticing it was missing after uploading means
rehashing, re-uploading under a new hash, and in the meantime the swarm has been announcing to DHT.
The `source` tag (default `EMP`) also goes in the info dictionary, which is what keeps the infohash
distinct per tracker for cross-seeding.

## Piece size

Chosen automatically from the payload size: the smallest power of two that keeps the torrent at
1500 pieces or fewer, between 16 KiB and **8 MiB — EMP's maximum; its upload checker rejects larger
pieces**. That lands on the conventional sizes:

| Payload | Piece size | Pieces |
|---|---|---|
| 700 MB | 512 KiB | ~1400 |
| 1 GiB | 1 MiB | 1024 |
| 2 GiB | 2 MiB | 1024 |
| 4 GiB | 4 MiB | 1024 |
| 8 GiB | 8 MiB | 1024 |
| 20 GiB | 8 MiB | 2560 |
| 67 GB megapack | 8 MiB | ~8,000 |

Each built torrent shows the size it used. An override in settings is a pick-list of valid sizes;
any other value, including one typed into the stored settings, is rounded to the nearest power of
two, because clients reject piece sizes that are not.

## Usage

1. **Queue** — select videos in the library and press **Add to EMP prep** in the selection toolbar,
   or use the same action from a single video's operations menu. Items land in a prep batch (the most
   recent one, or a fresh "Unsorted").
2. **Review** — open **EMP Prep** in the nav. Each item shows its derived release name, title, studio,
   performers, dotted tags and description, each with a copy button. Edits are stored as overrides;
   **Reset to library values** drops them and goes back to what Cove says.
3. **Screens** — **Generate screens** extracts frames at automatic timestamps, or **Pick timestamps…**
   opens Cove's scrubbing sprite sheet to choose the moments by hand.
4. **Host** — **Upload to hamsterimg** pushes every image to hamsterimg.net and rewrites the
   description with the hosted links. Until then the description carries a visible
   `[NOT UPLOADED: filename]` marker per image, so an incomplete set cannot go out as broken images.
5. **Torrent** — **Build torrent** hashes the payload and writes the `.torrent`, then offers it for
   download along with the folder to point your client at.

Screenshot extraction and hashing run as Cove jobs, so they survive navigating away and show
progress both in Cove's task list and on the item itself.

## Packs: several videos, one torrent

Select several videos in the library and use **Add to EMP prep**: they become one **pack** — one
torrent and one EMP post. (Each use of the action makes a new batch; a single video is uploaded on
its own, exactly as before.)

### How the files get into one torrent without moving anything

A multi-file torrent describes one folder and the files inside it. Your videos live wherever Cove
keeps them, so each pack gets a folder under the **pack folder** setting built from **hard links**:

```
<pack folder>\
  [Pornhub] Spyder445 - Megapack (24 Scenes) [1080p-4K]\
     scene one.mp4          ← hard link to the library file
     scene two.mp4
     ...
     Screens\
        scene one.jpg       ← that scene's contact sheet (EMP requires screens inside packs)
        scene two.jpg
```

A hard link is a second name for the same data, not a copy. The pack costs no extra space for its
videos, nothing in the library is moved or renamed, and deleting a pack folder later only removes
the extra names — Cove's files are untouched. Add the .torrent to your client with the pack folder
setting as the save path and it seeds in place.

The pack folder has no default; set it under **Settings → EMP Prep**. Two rules are enforced:

- **Same drive as the videos.** Hard links cannot cross drives (on Windows, not even two partitions
  of one disk). Hard links also work over a network share, as long as the pack folder is on the same
  share as the videos.
- **Outside every Cove library path.** Otherwise Cove's scanner would find the links and import every
  video a second time as a duplicate.

Rebuilding a pack after adding or removing scenes removes the links and screens EMP Prep made last
time that no longer belong. It only ever removes files it recorded creating.

### The pack post

Following EMP's megapack rules, the post is a summary rather than a write-up of every scene:

- **What the pack has in common** — your text, required by EMP, leading the description.
- **Stats** — scenes, total runtime and size, quality split (e.g. `4K 25% · 1080p 75%`), shortest,
  longest, average and median length, average file size, codecs, frame rates and release span.
- **Performers** across the pack, with how many scenes each is in.
- **Sources** — studios and sites, with counts.
- **Tags** — every tag with how many scenes carry it.
- **Screens** — each scene's contact sheet as a clickable thumbnail. Above 50 scenes a
  representative 15% is shown (capped at 100), as EMP allows when every screen is inside the torrent.

The title defaults to `[Studio] Performers - Megapack (24 Scenes) [1080p-4K]` — the studio only when
every scene shares it, "Various" for more than three performers. The Tags field gets every performer,
source site and quality, the content tags carried by at least 25% of the scenes, and `megapack` or
`minipack`. The cover is a chosen scene's cover image.

### Rule checks

Before you upload, the pack flags:

- fewer than 10 scenes — EMP discourages small minipacks;
- no theme text — EMP requires one;
- scenes without screens — **blocks** the torrent, since every video must have screens inside it;
- two entries that look like the same scene — EMP forbids two versions of one scene in a pack;
- many performers across many sources — EMP does not allow random collections;
- a pack folder that is unset, inside a library path, or on another drive — **blocks** the torrent.

## The EMP upload form

Each item has an **EMP upload form** section laid out in the same order as EMP's upload page, with a
copy button per field:

| EMP field | What EMP Prep gives you |
|---|---|
| Torrent file | The built `.torrent`, to download |
| Category | Left to you on EMP — pick it there |
| Title | `[Studio] Performer & Performer - Title (date) [1080p]`, editable |
| Tags | Dotted and space-separated, with a live count against EMP's minimum of 8 |
| Cover image | The first full-size image's hamsterimg URL |
| Description | BBCode built on EMP's section blocks |

**Tags** follow EMP's upload guide: multi-word tags are joined with dots (`doggy.style`), performers
use their full dotted names, and two tags libraries usually lack are added — the quality (`1080p`,
taken from the video's shorter side so vertical videos are labelled correctly) and the studio's
website domain (`pornhub.com`, from the studio's URLs in Cove). Release-date tags
(`2024 2024.05 2024.05.01`) and the codec (`h264`) can be switched on in settings.

**The description** follows the starter text EMP's form begins with,
`[quote][info][details][screens][/quote]`, treating each block as a section heading:

```
{featured}            ← the full-size images
[quote]
[info]                ← title, studio, performers, release date, and the scene description
[details]             ← the media info
[screens]             ← the contact sheet
[/quote]
```

EMP does not publicly document what its `[info]`, `[details]`, `[screens]`, `[cast]` and `[plot]`
blocks render, so check your first description in EMP's BBCode Sandbox. The layout is the
description template in settings; adjusting it needs no code change.

**Editing.** The title, tags and description can all be edited per item. Only fields you actually
change are stored as edits; everything else keeps following the library and your settings, so a
new tag rule or template still reaches the item. **Reset to library values** drops every edit.

## What gets uploaded

Each video uploads three images to hamsterimg, not every frame:

- **Two full-size images** at the top of the description: the video's cover art from Cove, plus one
  frame from the middle of the video. Without a cover, two frames are used, taken at the thirds of
  the video. The first full-size image is also shown as the **Cover image URL** for the upload form.
- **The contact sheet**, a grid of every extracted frame with its timestamp in the corner of each
  tile, as the screens section.

The individual frames stay on disk. They exist to build the contact sheet, and any one of them can
be starred on an item to upload it full size in place of another. The number of full-size images is
a setting (default 2).

Images go to **hamsterimg.net** through its upload API, flagged `nsfw=1`. Set the API key from your
hamsterimg account (Settings → API) under **Settings → EMP Prep**, plus an optional album id. A
failed image is retried on the next upload without re-uploading the ones that already made it.

## Screenshots come from the source file, not the sprite sheet

The sprite sheet is used for **picking timestamps** only. Cove composes sprites out of 160px frames
for scrubbing previews, so a cropped tile is a thumbnail and would make a poor upload screenshot.
Chosen timestamps are re-extracted from the source file at its native resolution.

If a video has no sprite sheet, the picker says so and automatic spacing is used instead — a missing
sheet never blocks a prep run.

### How many frames

One frame per `SecondsPerFrame` (default 90s), but never fewer than `MinFrameCount` (default 12):

| Runtime | Frames | Why |
|---|---|---|
| 3 min | 12 | Spacing gives 2, so the minimum takes over |
| 10 min | 12 | Spacing gives 7, so the minimum takes over |
| 45 min | ~30 | Spacing wins — about one every 90s |
| 4 h | 60 | Capped by `MaxFrameCount` |
| 30 s | ~6 | Too short to space 12 frames 4s apart |

The first and last 2% are skipped so frames miss studio intros and credits. All of these are
settings.

## Settings

Under **Settings → EMP Prep**:

- **Tracker** — announce URL (with your passkey; required before a torrent can be built), source tag,
  piece size (Automatic by default — see above), optional comment.
- **Output** — artifact directory (default: `.empprep-data\prep` inside Cove's extensions folder),
  the **pack folder** (no default; same drive as the videos, outside every library path), and
  whether single videos are also built as a folder there.
- **Screenshots** — spacing, min/max count, minimum gap, intro/outro trim, format, JPEG quality, and
  an optional width (0 keeps the source resolution, which is what a tracker wants).
- **hamsterimg.net** — API key and optional album id. Uploading publishes your images and only ever
  happens when you press **Upload to hamsterimg**, never as part of generating them.
- **Packs** — the title and description templates, the share of scenes a content tag needs to make
  the Tags field, and whether to tag megapack/minipack.
- **Uploaded images** — how many full-size images per video, whether the Cove cover is one of them,
  and the contact sheet's columns and tile width.
- **Tags & templates** — lowercasing, whether performers and studio become tags, an exclusion list,
  and the release-name and description templates.

### Templates

Release name placeholders: `{studio}` `{performers}` `{title}` `{date}` `{resolution}` `{height}`
`{codec}` `{source}`.

Description placeholders (`{featured}` is the full-size images, `{screenshots}` the contact
sheet, `{cover}` the first full-size image's bare URL): `{title}` `{studio}` `{performers}` `{date}` `{details}` `{tags}`
`{mediainfo}` `{featured}` `{screenshots}` `{cover}` `{duration}` `{resolution}` `{filename}` `{filesize}` `{code}`
`{director}` `{releasename}`.

A placeholder that resolves to nothing is collapsed out of the release name, so a video with no
studio does not produce a name starting with `" - "`.

## Tag formatting

Words inside one tag are joined with dots; tags are separated by spaces. `Big Tits` becomes
`big.tits`, and the list becomes `big.tits anal riley.reid`. Runs of punctuation collapse to a single
dot (so `Big  Tits`, `Big - Tits` and `Big.Tits` all give `big.tits`), accents are stripped so a tag
matches its plain spelling, and duplicates are removed while preserving order.

## Requirements

- **ffmpeg** — required for screenshots. Uses whatever binary Cove is configured with, falling back
  to `PATH`. The settings panel shows which one it found.
- **ffprobe** — optional; fills in audio channel and sample-rate detail in the media-info block.

Extension tables (`empprep_batches`, `empprep_items`) live in Cove's PostgreSQL database. Batches are
per-account.

## What this does not do

It does not submit to the tracker. It produces the `.torrent` and the copy-ready fields; the upload
form is yours to fill. It also does not add the torrent to a client — building it never touches your
client's state.

## Layout

```
EmpPrep.Extension/
├── Directory.Build.props        # net10.0, CoveMinVersion, plugin output settings
├── Directory.Build.targets      # Cove host contracts, compile-only; host-assembly strip targets
├── EmpPrep.Extension.slnx
├── package.json                 # esbuild + @tailwindcss/cli (UI build)
├── tsconfig.json                # type-checks the UI against the sibling cove checkout
├── shared/theme.css             # Tailwind theme
├── scripts/
│   ├── build-extension-ui.mjs   # bundles ui/EmpPrep.tsx -> dist/bundle.js (+ scoped bundle.css)
│   └── stage-local-extension.ps1
├── src/EmpPrep/
│   ├── EmpPrep.csproj
│   ├── extension.json           # manifest and the single source of truth for identity/metadata
│   ├── EmpPrepExtension.cs      # entry point: DI, migrations, HTTP API, UI manifest, actions
│   ├── Data/                    # prep batch/item entities + PostgreSQL schema and migration
│   ├── Models/                  # settings and the derived/assembled prep DTOs
│   ├── Services/                # torrent builder, bencode, screenshots, sprites, metadata, uploads
│   ├── ui/EmpPrep.tsx           # React page + settings panel
│   ├── ui/EmpPrep.css           # scoped styles
│   └── dist/                    # built UI bundles (checked in)
└── tests/EmpPrep.Tests/
```

## Install into Cove

```powershell
npm install
.\scripts\stage-local-extension.ps1
```

That builds the UI bundles and the backend, then copies the output into Cove's extensions
directory — by default `%LOCALAPPDATA%\cove_personal_live\extensions\cove.community.empprep`. Point
it elsewhere with `-DestinationRoot` if your Cove instance uses a different data directory.

**Cove only discovers extensions at startup, so restart it afterwards.** "EMP Prep" then appears in
the nav, and its settings under **Settings → EMP Prep**.

Set the announce URL before anything else: torrent building refuses without it.

### If it does not appear

Discovery is silent about a directory it cannot read, so check in this order:

1. `dotnet test .\tests\EmpPrep.Tests\EmpPrep.Tests.csproj` — `ManifestTests` and
   `HostDiscoveryTests` run the host's own manifest parser and discovery over a staged copy, so a
   manifest or entry-point problem fails there rather than silently in Cove.
2. `extension.json` must be present in the staged directory next to `EmpPrep.dll`.
3. Cove's log (`<cove data dir>\logs\`) for `Failed to process extension directory`.

A manifest the host cannot deserialize is the failure mode worth knowing about: every nested object
in it has required fields (`externalDependencies` entries need both `id` and `name`), and one
missing field throws for the whole file, which skips the directory entirely with no extension shown
and nothing in the UI to explain it.

Note on `minCoveVersion`: it is compared with semver rules, where a prerelease sorts *below* the
release of the same number. A local Cove dev build reports something like `1.4.2-dev.102`, so
declaring `1.4.2` would be **refused** by it. This manifest declares `1.4.1` to stay clear of that;
`HostDiscoveryTests` asserts the running dev line satisfies it.

## Build

```powershell
npm run build:ui                       # bundles src/EmpPrep/ui -> src/EmpPrep/dist
dotnet build .\EmpPrep.Extension.slnx -c Debug
```

### Cove host references

`extension.json` is the single source of truth for the extension's identity — none of it is
redeclared in C#. The Cove host contracts (`Cove.Sdk`, `Cove.Core`, `Cove.Data`) are referenced
centrally by `Directory.Build.targets`, compile-only, and never shipped: the host owns the single
loaded copy, and a bundled duplicate would split type identity inside the extension's load context
at runtime.

- **Local dev:** with a sibling `../cove` checkout, the contracts resolve by `ProjectReference`, so
  changes flow without a NuGet bump. This repo must sit beside `cove` for that (and for the UI
  type-check and the tests, which compile against it).
- **CI / package mode:** otherwise they resolve from the published `Cove.*` NuGet packages at
  `CoveMinVersion` (1.4.2). Force it locally with
  `dotnet build -p:UseLocalCoveSource=false -p:UseLocalCoveCore=false`.

`Cove.Data` is in this repo's `CoveHostProvidedAssemblies` list in addition to the SDK's own
defaults, because this extension owns tables inside Cove's database and therefore compiles against
`CoveContext`.

## Verification

```powershell
dotnet test .\tests\EmpPrep.Tests\EmpPrep.Tests.csproj
..\cove\ui\node_modules\.bin\tsc.cmd --project tsconfig.json
npm run build:ui
```

203 tests cover the bencode encoding byte-for-byte (key ordering and byte-length prefixes, because the
infohash depends on both), piece hashing including across file boundaries, the private flag, tag
dotting, the frame-count rule at each boundary, description rendering, sprite VTT parsing, the
hamsterimg response parser, which images are uploaded and how they render, pack layout, stats, title, tags and rule checks, and the PostgreSQL model and raw migration agreeing with
each other. A pack test builds a real folder of hard links plus Screens and hashes it.
They also run the host's own manifest parser and its `DiscoverExtensions` over a staged copy of the
build output, so an extension Cove would silently skip fails the suite instead.

The torrent output has also been checked end-to-end against an independent bencode decoder: a
140 MB file produced 1120 pieces that all verified against the source bytes, with `private=1`, a
canonically ordered info dictionary, and an infohash matching independent re-encoding. Extraction was
verified frame-accurate against a clip with a burned-in timer.

Full verification inside a running Cove instance is separate and has not been done here.
