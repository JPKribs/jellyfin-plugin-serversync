# Links

A library mapping can hold the source's files as **links** instead of full copies. A link is a small `.strm` file written where the copy would have gone. It appears in the library at once, uses almost no disk, and plays by streaming the file from the source through Server Sync on both servers.

Links suit a library you want available everywhere but do not want to store twice, such as a large archive, or a server with little disk. They need the source to be reachable whenever someone plays, and every play streams across the network.

!!! info "Both servers run Server Sync"
    Links need Server Sync on the source and on the server that holds the links, both on 12.2.10.0 or later. A source without it, or with linking switched off, never serves a link, and a mapping set to Link waits until it does.

## Terms

| Term | Meaning |
| :--- | :--- |
| Source | The server that holds the real file. |
| Linking server | The server that holds the link. Its library mapping is set to **Link**. |
| Link | A `.strm` Server Sync wrote for a source file. A `.strm` you made yourself is never treated as a link. |

## How It Works

1. On the linking server, a library mapping is set to **Link**. **Sync Content** writes a `.strm` for each new video instead of downloading it, under the same name with only the extension changed: `Film (2020) - 4K.mkv` on the source becomes `Film (2020) - 4K.strm`. An `.nfo` with the source's details and any subtitle files are written beside it.
2. The `.strm` holds one line: an address on the linking server itself, `http://127.0.0.1:{port}{base URL}/Sync/Link/{id}?k={secret}`. No credential for the source is ever written into a file.
3. Jellyfin adds the link to the library. Server Sync then gives it the media info the source already holds for the file, so it shows its runtime, codecs, and tracks before anyone plays it.
4. When someone presses play, the linking server asks the source whether that user may stream the file. If not, playback stops at once with the source's reason.
5. The player reads the file through the linking server's own stream route. Server Sync fetches the bytes from the source with the linking server's own credentials, passing seeks through as byte ranges. Direct play is streamed for the user who asked. A transcode reads the link's address, as Jellyfin's ffmpeg does for any `.strm`.
6. The source counts the stream against its limits and shows it on its dashboard as a session, for the user it streams as. Stopping updates played state there.

Playback works with direct play, direct stream, and transcoding on the linking server. The client never sees the link's address.

## Setting Up

### On the source

Open the Servers tab and the entry for the linking server. The entry must exist, since that is how the source knows the linking server and pairs with it. Its direction can be any. Under **Connection**:

* **Allow Linking** lets that server stream this server's files for its links. Off by default.

With it on, these appear:

* **Link Streams At Once** is the most streams that server may hold at once. 0 means no limit.
* **Link Streams Per User** is the most streams one user here may hold at once through that server. Their link streams through every linking server count toward it. 0 means no limit.
* **Count Their Own Playback** counts a user's playback on this server toward their per user limit.
* **Honor Remote Bitrate Limits** refuses a file above a user's **Maximum remote streaming bitrate**, since a link streams the file as it is. A film held in several versions still plays in a version under the limit. See [Versions](#versions).
* **Link Stream Speed (Mbps)** is the most each stream to that server is sent at. Set it above the bitrate of the files you share, or playback buffers. 0 means no limit. The first few seconds of a stream are sent at full speed, so playback starts at once.
* **Scheduled Link Stream Speed (Mbps)** is used instead during the hours of the bandwidth schedule on the Settings tab, while that schedule is on. Downloads and link streams share the schedule's hours, each with its own speed.
* **Stream Unmapped Users As** is the user here who streams for a user on that server with no user mapping here. **None** refuses them.

Save the server. The source then advertises links to servers that ask.

### On the linking server

Open the entry for the source. Under **Libraries**, set **Storage** on a mapping:

* **Copy** downloads each file. This is the default and how content sync always worked.
* **Link** writes a `.strm` for each new video.

Links are written by **Sync Content** like any other file, with the same **Approvals** for new files and the same filters. Only video is linked: movies, episodes, videos, music videos, and trailers. Audio, books, and anything else in a Link mapping is copied, because Jellyfin only plays video from a `.strm`.

!!! tip "When the source has not allowed linking yet"
    New files in a Link mapping wait, and the log says the source does not offer links. Once the source allows linking, the next refresh lists that source in full and links them.

## Who the Source Streams As

The source decides who each stream runs as, in this order:

1. **The source's own user mapping** for the linking server. On the source, the **Users** step of the linking server's entry maps that server's users (left) to users here (right).
2. **Stream Unmapped Users As**, when set.
3. **The linking server's mapping**, but only when the source maps no users for it and names no fallback, since the linking server's key already reaches the source.

With none of these, the user is refused. A source entry that holds a standard user's key streams as that user only.

That user must exist, be enabled, be allowed to play media, and be able to see the item, including its parental rating. These are the source's own rules, checked on every play.

## Limits

The source counts a stream while a request for it is open, and for 90 seconds after the last one ends, so pauses and seeks never count twice. Each user and item is one stream. Limits are checked when play is pressed, and again under one lock as the stream opens, so two plays at the same moment cannot pass a limit together.

A user refused for a limit sees the reason when they press play, unless another server that holds the same file allows them. See [When the Source Cannot Stream](#when-the-source-cannot-stream). In the rare case that two plays race for the last stream, the one that loses fails as it starts, without the reason.

## Versions

When a film is held in several versions, such as `Film (2020) - 4K.mkv` and `Film (2020) - 1080p.mkv`, each version is a link of its own. Pressing play asks the source about every version. The versions it refuses the user, such as one above their bitrate limit, are left out of what their player is offered, and the film plays in a version it allows. The play is refused only when no version is allowed, with the reason for the first.

## What Gets Written

### The .strm

One line with the link's address. It is rewritten when it is out of date, such as after this server's port or base URL changes or its link key is replaced, and written again if it is deleted. A `.strm` that no longer points at Server Sync, because it was edited or replaced, is left alone and its row shows why.

### The .nfo

An `.nfo` with the same name, from the source's details: title, original title, year, dates, plot, tagline, parental rating, runtime, genres, studios, tags, and ids such as IMDb, TMDB, and TVDB. For an episode it adds the show, season, and episode number. Jellyfin reads it, so the link is identified as the film or episode it is rather than by its name.

A comment just below the `<?xml …?>` line says it was written by Server Sync. Only an `.nfo` with that comment is ever replaced, so:

* An `.nfo` already there that Server Sync did not write is left alone.
* Deleting that comment line keeps your own edits from then on.

Jellyfin reads an `.nfo` again on a full metadata refresh. While Server Sync's `.nfo` is in place, a full refresh can put edits made on this server, or by metadata sync, back to the source's values. Delete the comment line, or the `.nfo`, to hand the metadata to Jellyfin. An `.nfo` you delete is not written again.

### Subtitles

With **Include Companion Files** on, external subtitle files are downloaded beside the link, as they are for a copy. Subtitles inside the file, such as SRT, ASS, or PGS tracks in an MKV, are listed from the link, and Jellyfin extracts or burns them in by reading the file through the link. The first extraction of a text track reads the file once, and Jellyfin caches the result.

### Media info

Jellyfin never probes a `.strm` during a scan. The source already holds the file's runtime, container, streams, attachments, and chapters, so Server Sync asks the source for them about 20 seconds after the library adds the link and saves them on the link. Nothing is read from the file itself. Two minutes later it checks that the media info stuck, and asks again if a busy library scan saved over it, up to three times.

When the source cannot give it, such as a source on an older release, the file is probed through the link instead. The probe may read up to 256 MB of a file, which is far more than a probe needs and far less than a film, so nothing else on the linking server reads whole files from the source through a new link.

Every **Sync Content** run also gives any link still without media info the source's, so a link missed while a server was restarting is caught on the next run. Jellyfin probes a link again each time someone plays it, as it does for any `.strm`.

### Trickplay and chapter images

Jellyfin makes neither for a `.strm`, whatever the library's settings say. With **Take Link Images From the Source** on in Settings, which is the default, a link takes the trickplay tiles and chapter images its source already made, along with its media info. A source that made none has none to give.

* **Trickplay** works with trickplay extraction on or off in the linking server's library. Jellyfin leaves trickplay out of the details of an item whose file is remote, which a `.strm` is, so Server Sync lists a link's trickplay images in those details itself.
* **Chapter images** are only kept while **chapter image extraction** is on for the linking server's library. With it off, Jellyfin drops them on its next refresh of the item, and **Sync Content** logs a warning naming the library. Jellyfin also resets a link's chapters when it scans the library and reads them again from the file at the next play, so chapter images can be missing between a scan and a play.

## When the Source Cannot Stream

A link streams from the server that won its file in sync: the first server on the linking server's Servers tab that offers it. When two servers hold the same film in folders that map to the same place, only one row and one link are made, as for copies.

When that server is down, at its limits, or refuses, the linking server tries the others it pulls from, and uses the first in the Servers tab's order that allows the play. The others are asked together, so one slow server does not hold up the rest. When the link's own server takes more than two seconds to answer, the others are asked alongside it. A server that did not answer is passed over for the next minute while another can play the file, so only the first play after a server goes down waits on it. A server is tried when one of its enabled library mappings covers the folder the link sits in. It is asked for the file at the matching path there, with the same checks as the link's own server, and it is used only when it allows the user and its file is the same size. The link file itself never changes, and the next play asks the link's own server first again.

This happens at three moments:

* **When play is pressed.** A source that cannot be reached or refuses is passed over before playback starts.
* **When the stream opens.** A source that fails between pressing play and the first read, or refuses as the stream opens, is passed over before any of the file is sent. Later reads of that playback stay on the server that stood in.
* **Partway through.** When the source drops the stream, or sends nothing for 30 seconds while the player is waiting, the rest is asked for again from where it stopped: from the same server first, then from another that holds the same file. The player gets one unbroken file. This is tried up to three times for one read, after which the player sees the stream end, and pressing play again falls back.

The server that stood in sees the playback as its own: it shows the session, records the play, applies its own limits, and may control the viewer. The link's own server may not control that viewer.

Each server applies its own rules. A user one source refuses, for example for a parental rating, plays from another source that allows them.

When no server allows the play, the refusal is the link's own server's reason, followed by the others tried, such as `(also tried source-b)`. A server whose file at that path is a different size is listed as tried.

## Playback on the Source

Playback of a link shows on the source as a live session named after the linking server and the viewer's client, under the client name **Server Sync**, for the user it streams as. From the source's dashboard:

* **Stop, pause, resume, and seek** reach the viewer on the linking server. So does **Send Message**, which is shown with the source's name. This needs the source's entry for the linking server to have an address and a key.
* The activity log and playback plugins on the source see the play.
* Stopping updates played state, play count, and resume position on the source through Jellyfin's own rules.

Progress is reported every 30 seconds, and at once on a pause or resume. The session ends when playback stops. If the linking server goes away, Jellyfin stops the playback after five minutes without a report, and the session closes ten minutes after the last report.

Jellyfin's own stop of a paused session on the source is not passed on, so the source's housekeeping never stops someone watching elsewhere.

History sync takes the larger play count when both servers counted, so a play counted on the source by its session and on the linking server by Jellyfin stays one play.

## The Link Check

Every **Sync Content** run asks each connected server about the links that stream from it. A link whose file is gone from the source, or whose source no longer allows linking, shows on the Sync tab as an error starting with `Link check:` and the source's reason, before anyone presses play. The `.strm` is left in place, and the row is settled again on the first run where the source can stream the file. A source that cannot be reached flags nothing, since play falls back to another server and the next run checks again. What happens to a link whose file was removed for good is still decided by **Removed files** on the next refresh.

The check also keeps each link's recorded size current, which is how another server's copy is recognized as the same file.

## Turning Links Into Copies

A link becomes a full copy in either of two ways:

* **Turn links into full copies** on the Sync tab's toolbar, for the selected rows. These are queued at once, whatever **New files** is set to, since choosing them is the approval.
* Setting the mapping's **Storage** back to **Copy**. The next refresh marks its links under the **New files** approval: queued when new files download on their own, waiting as Pending Replacement when they need approval, and left as links when new files are switched off. Setting it back to Link before approving keeps them links. A link still waiting for its own first approval is left until that is settled.

The next Sync Content run downloads the copy through the normal download path, with its disk space and validation checks. The `.strm` is removed only once the copy is in place, so the link keeps playing until then. If the download fails, the link stays and the row retries. Server Sync's `.nfo` and the subtitles stay beside the copy, since they describe the same film. Jellyfin sees one item removed and one added, and history sync carries watched state across.

Copies already in a library stay copies when its mapping is set to Link.

## The Sync Tab

Link rows show **(Link)** after their status, and links on their way to a copy show **(Link to Copy)**. **Mark Synced**, **Ignore**, **Queue**, and **Delete** work as for copies. Deleting a link removes the `.strm` and, as for a copy, every file beside it under the same name: its `.nfo`, subtitles, and images.

## Matching Across Servers

Every module that matches by path still matches when one server holds `Film.mkv` and the other `Film.strm`:

* On the linking server, a path that finds no file finds the link standing in for it. A real file at that path always wins.
* When another server reads the linking server's items, it asks which ones are links and matches each by the file it stands in for. A peer's links are never copied as content.
* Changes announced live for a link carry the copy's path, so a peer on any version matches them.

## Security

* **The link's address only answers the linking server itself**, from and to its own loopback address. A request that carries a proxy header (`X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `X-Real-IP`, `Forwarded`, or `Via`) is refused, so a reverse proxy on the same machine cannot reach it.
* **Each link carries a secret** derived from a key only this server holds, so a link cannot be guessed from its id. The key is `serversync-link.key` in Jellyfin's data folder, readable by the server's account only.
* **The address never reaches a client.** It is cleared from playback info and from every item response, and Jellyfin's routes that hand out an item's own file, **Download** and its file route, refuse links.
* **Only a user the source allowed can be read for.** A user is recorded when the source allows them at play. Stream, transcode, subtitle, and attachment requests on a link must come from a user who holds that permission, so no one can stream a link under another viewer's permission. A permission nobody uses expires after two minutes. Once in use it lasts an hour after the last read.
* **No source credential is ever written to disk.** The linking server streams with the key on its entry for the source, and the source checks the user it streams as on every play.

## Troubleshooting

When the source refuses, the player shows `This item streams from another server, which refused:` followed by the reason.

| Reason | What to do |
| :--- | :--- |
| linking is not allowed for *server* here | Turn on **Allow Linking** on the source's entry for the linking server |
| your account is not mapped to a user on the source | Map the user on the source, or set **Stream Unmapped Users As** |
| the account you stream as on the source is missing or disabled | Fix the mapped user on the source |
| the account you stream as on the source may not play media | Turn on media playback for that user on the source |
| the account you stream as on the source cannot see this item | Give that user the library, or a parental rating that allows the item |
| the file's bitrate is above the *n* Mbps the source allows your account | Raise the user's remote bitrate limit on the source, or turn off **Honor Remote Bitrate Limits** |
| the source allows *n* link stream(s) at once and all are in use | Wait, or raise **Link Streams At Once** |
| the source allows your account *n* stream(s) at once and all are in use | Wait, or raise **Link Streams Per User** |
| the file is no longer on the source | The next refresh handles it as a removed file |
| *source* could not be reached | The source is down or unreachable from the linking server |
| the source answered *status*: … | The source answered but would not check: `404` means Server Sync there has no links, `409` that it does not list the linking server, and `428` that the two are not paired. Use **Check Link** on the linking server's entry for the source |
| the source's key for this server belongs to another user | The source's entry holds a standard user's key, which only streams as that user |
| the item is a link on the source, not a file | The source holds a link too. A link is never streamed from another link |
| the server this link streams from is no longer set up here | The source's entry was removed or turned off on the linking server |
| … (also tried *servers*) | The link's own server refused for the reason given, and each server listed holds the folder but refused too, was unreachable, had no file at that path, or had a different file there. See [When the Source Cannot Stream](#when-the-source-cannot-stream) |
| the file is not on this server | Shown for a server tried in place of the source, which has no item at the matching path |
| this server could not tell who is playing | The request carried no signed in user |
| press play again, so this server can ask the source | The client streamed without asking for playback first. See Limitations |

A link row that errors on the Sync tab says why:

| Reason | Meaning |
| :--- | :--- |
| Link check: *reason* | The last content sync found the source cannot stream the file. See [The Link Check](#the-link-check) |
| A full copy is already at *path* | A real file has that name. It is tracked as a copy instead |
| Another source file would link to *path* | Two source files differ only by extension, so only one can be linked |
| A .strm that Server Sync did not write is already at *path* | A `.strm` there no longer points at Server Sync, so it is left alone |
| *path* is outside every mapped library | The path escapes the mapping's root |

## Limitations

* **A source must be up.** A link cannot play while its source is unreachable, unless another server it pulls from holds the same file. Every play uses the network between the servers.
* **Clients must ask before they stream.** Clients that open a stream without first asking Jellyfin for playback, or without a token, are refused on links. Jellyfin's own apps ask first. Some DLNA renderers and external players do not.
* **Two viewers transcoding one link at once** are both checked by the source, but the source counts the transcodes under the one seen most recently, since Jellyfin's ffmpeg reads a link without saying who for. Direct play is streamed, counted, and limited for each user. **Stream Direct Play of Links Per User** in Settings turns that off, should a player not direct play links with it on.
* **No transcoding on the source.** A link streams the file as it is. A user over their bitrate limit is refused rather than transcoded on the source. The linking server can still transcode.
* **Trickplay and chapter images** are only what the source made. Jellyfin makes none for a link.
* **Only video is linked.** Audio and books in a Link mapping are copied.
* **Renames on the source** give the file a new id there, so the new name is linked as a new file and the old link follows **Removed files**, as a copy would.

## Upgrades and Downgrades

Links add one column to Server Sync's database. Updating keeps every row as it was, and copies stay copies.

Once a server holds a link, its database tells older releases not to use it. An older release then sets the database aside and starts fresh rather than mistake a `.strm` for a broken copy and download over it. The old database is kept beside the new one as a `.newer-*` backup. The `.strm` files are left as they are, and the older release downloads full copies beside them. A server that never held a link can be downgraded as before.
