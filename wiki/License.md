# License

This project is released under the [MIT License](https://github.com/canerztk8/NitroLite/blob/main/LICENSE).

The interface is drawn in eight typefaces, all embedded in the executable and all licensed under the SIL Open Font
License 1.1, which travels with the package:

| Face | Upstream |
|---|---|
| Quicksand | [andrew-paglinawan/QuicksandFamily](https://github.com/andrew-paglinawan/QuicksandFamily) |
| Exo 2 | [google/fonts](https://github.com/google/fonts/tree/main/ofl/exo2) |
| Oxanium | [google/fonts](https://github.com/google/fonts/tree/main/ofl/oxanium) |
| Rubik | [google/fonts](https://github.com/google/fonts/tree/main/ofl/rubik) |
| JetBrains Mono | [JetBrains/JetBrainsMono](https://github.com/JetBrains/JetBrainsMono) |
| Rajdhani | [google/fonts](https://github.com/google/fonts/tree/main/ofl/rajdhani) |
| Space Grotesk | [floriankarsten/space-grotesk](https://github.com/floriankarsten/space-grotesk) |
| Chakra Petch | [m4rc1e/Chakra-Petch](https://github.com/m4rc1e/Chakra-Petch) |

Each was subset to the character ranges the eight catalogues can put on screen, and each was instanced at three
weights because Avalonia 11 exposes no variation axis. None of the eight carries a Reserved Font Name, so the subset
keeps the family name. The per-face copyright line and the exact change made are in `third-party/<face>/NOTICE.md`,
and the combined notice is in `THIRD-PARTY-NOTICES.md`.

The package also contains [WinMemoryCleaner](https://github.com/IgorMundstein/WinMemoryCleaner) 3.0.8, which is
GPL-3.0. It is an unmodified, separate executable that NitroLite only launches as a child process, so the two form an
aggregate under GPL-3.0 section 5 and NitroLite's own code stays MIT. The package carries the tool's licence, a notice
and its source archive; see `THIRD-PARTY-NOTICES.md`.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
