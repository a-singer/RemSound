# RemSound v3.0.2

Hot-fix for a slow memory leak on the receiving side. If you leave RemSound running for many hours receiving audio, its memory use was creeping up over time. Small at first, big enough after a day to slow the computer down and make audio feel slightly laggy.

## What was happening

RemSound uses a small library called Concentus to decode incoming Opus audio. In version 2.2 we switched from a pure software version of that library to a faster native one. The native version keeps some memory in Windows itself rather than in RemSound, and you're supposed to tell it explicitly when you're done with it. Our code wasn't doing that — it was relying on the system to notice and tidy up eventually, but a setting we use to keep audio smooth also stops Windows from doing that tidy-up.

So memory built up steadily over hours and never came back. A user left RemSound running for nearly a full day receiving audio and his desktop ended up using 3.5 gigabytes of memory before he restarted it. That was getting in the way of everything else on the computer and was almost certainly the cause of a feeling that audio latency was "drifting" over a long session — at that point the CPU was working hard enough that audio scheduling wasn't as tight as it should be.

## What's fixed

Two things. First, the part of RemSound that uses Concentus now properly releases its memory at the right moments — when a session ends, when you change codec, and when a recording stops. That stops the leak at its source.

Second, as a safety net in case anything else in RemSound or any other library we use ever has the same kind of bug, RemSound now does a quick "release any leftover memory" pass once every five minutes. The pass runs in the background, doesn't affect audio in any way, and finishes long before the next audio packet arrives.

## What to expect

After updating, your memory use should settle at around 100-200 megabytes and stay there as long as you leave RemSound running. You can leave it running overnight, or for days, and the memory should hold roughly flat instead of climbing.

If you noticed audio feeling slightly laggier after long sessions and had been working around it by restarting RemSound, that workaround should no longer be needed.

## Nothing else has changed

Same wire format as v3.0 and v3.0.1, same codec list, same everything else. v3.0.2 talks to other v3.0.x machines exactly as before. If you've not noticed any slowdown after long sessions, the fix is still worth having because the leak was happening under the surface even if you didn't see it.

## Install

1. Download `RemSound-v3.0.2.zip` from this release.
2. Close RemSound.
3. Extract the zip over your existing RemSound folder, overwriting program files when prompted. The zip is program files only — it will not touch your profiles, settings or recordings.
4. Run `RemSound.exe`. Press F1 for the user manual.

Requires the .NET 10 Desktop Runtime. If it's missing, Windows offers to fetch it on first launch.

## Upgrading

**v1.9, v2.0, v2.1, v3.0, v3.0.1:** Help → Check for updates works — it will fetch and install v3.0.2 automatically. If you've ticked "Check for updates on startup" and "Silently install updates", v3.0.2 installs itself shortly after launch and RemSound reopens on whichever profile you were running (a feature added in v3.0).

**v1.8 and earlier:** the auto-updater in those versions has a fault that prevents it from installing updates, so Check for updates will download v3.0.2 but not apply it. Install v3.0.2 by hand using the steps above — just this once. From the build you install onward, updates are automatic.
