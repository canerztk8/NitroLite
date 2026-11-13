# WinMemoryCleaner

NitroLite ships an unmodified copy of WinMemoryCleaner so that the memory card works
out of the box, without a separate installation step.

| | |
|---|---|
| Project | https://github.com/IgorMundstein/WinMemoryCleaner |
| Author | Igor Mundstein |
| Version | 3.0.8 |
| Licence | GNU General Public License v3.0 (`LICENSE` in this folder) |
| Source | https://github.com/IgorMundstein/WinMemoryCleaner/tree/3.0.8 |
| Source archive | https://github.com/IgorMundstein/WinMemoryCleaner/archive/refs/tags/3.0.8.zip |
| SHA-256 | `8b68d56c6ee28740f76513f21b21f5a1018f0ef291467b3f3d39d43daf0c0f2f` |

## Why the two licences do not conflict

NitroLite is MIT-licensed. WinMemoryCleaner is GPL-3.0. They are distributed together
as an **aggregate**, which GPL-3.0 section 5 explicitly permits:

> A compilation of a covered work with other separate and independent works, which are
> not by their nature extensions of the covered work, and which are not combined with it
> such as to form a larger program, in or on a volume of a storage or distribution medium,
> is called an "aggregate" if the compilation and its resulting copyright are not used to
> limit the access or legal rights of the compilation's users beyond what the individual
> works permit. Inclusion of a covered work in an aggregate does not cause this License to
> apply to the other parts of the aggregate.

The two programs meet that definition:

- They are separate executables. NitroLite never links against WinMemoryCleaner, never
  loads it as a library, and shares no code with it.
- They communicate at arm's length: NitroLite starts the tool as a child process with a
  command-line argument (`/StandbyList`) and reads its exit code. Nothing more.
- The shipped binary is byte-for-byte the upstream release. It is not patched, renamed,
  re-signed or rebuilt.
- NitroLite's own source stays under MIT. Nothing in the aggregate restricts the rights
  GPL-3.0 grants to WinMemoryCleaner's users.

## What this means for you

- **WinMemoryCleaner** remains GPL-3.0. You may copy, modify and redistribute it under
  those terms. Its source is at the URL above.
- **NitroLite** remains MIT. See the `LICENSE` file in the package root.
- If you replace the bundled `tools/WinMemoryCleaner.exe` with your own build, that build
  is yours to license; NitroLite only launches whatever sits at that path.

## Updating the bundled copy

`scripts/publish-release.ps1` pins both the version and the SHA-256 digest. To move to a
newer release, change `$WinMemoryCleanerVersion` and `$WinMemoryCleanerSha256` there, then
update the table above. The build fails loudly if the downloaded file does not match the
pinned digest, so a tampered or re-uploaded asset cannot slip into a package.

## Not legal advice

This notice records the reasoning behind the packaging decision. It is not a legal
opinion. If you plan to redistribute NitroLite commercially, have a lawyer review it.
