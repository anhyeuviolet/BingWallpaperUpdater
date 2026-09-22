<!-- Release notes are written by hand for every release, from this skeleton, into the draft release that CI opens for the tag. CI never reads this file (generate_release_notes: false). Delete these HTML comments and every hint line before publishing. -->

# Highlights

<!-- Two to five bullets: what a user gets from this version. For the first release: what the app is (tray app, Bing daily image, under 100 MB, no admin, no telemetry). -->

# Install

<!-- Download BingWallpaperUpdater-X.Y.Z-x64-Setup.exe below and run it. Keep this sentence: "SmartScreen shows 'Windows protected your PC' because the installer is not code-signed: click More info, then Run anyway. Do not run the installer as administrator." Link the README Install section for the details. -->

# Verify the download

<!-- Paste the SHA-256 from the .sha256 asset (the first field of the file), in a code block, and the one-line check:
Get-FileHash .\BingWallpaperUpdater-X.Y.Z-x64-Setup.exe -Algorithm SHA256
State whether a build-provenance attestation exists for this tag (only for tags built while the repository is public). -->

# Changes

<!-- Since the previous release: user-visible changes first, then fixes, then internal changes worth a line. For the first release write "First public release." and stop. -->

# Known issues

<!-- Copy what is honest today from README > Memory (known issue) and the FAQ: e.g. the GDI object growth per Settings open/close, Windows 10 1809 not yet measured, screenshots not yet in the README. -->

# Memory

<!-- Copy the table header and the row(s) from README > Memory verbatim (the -Summarize output for docs/soak/*.csv on the build this tag was cut from); never type a number by hand. Mention the measured duration and method in one sentence. -->

# Supported Windows

<!-- Windows 11 (x64); Windows 10 LTSC/Enterprise 1809 and later (x64); other Windows 10 builds best effort. The installer refuses to run below build 17763. -->
