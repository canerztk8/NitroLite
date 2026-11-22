# Why SmartScreen warns

The prompt is about **provenance, not detection**. It does not mean Microsoft found something in the file.

- A browser tags every download with the **Mark of the Web** (an NTFS alternate data stream, `Zone.Identifier`).
  Windows sees that tag and runs the file through SmartScreen before the first launch.
- SmartScreen looks for an **Authenticode signature** and for **reputation** attached to the file's hash and its
  publisher. NitroLite has neither: a code-signing certificate that SmartScreen accepts costs a few hundred dollars
  a year and needs a registered business, which this project does not have.
- An unsigned binary has no publisher to accumulate reputation against, so every new build starts at zero and is
  reported as unrecognised. That is the whole of the warning.
- You can confirm the file is unsigned rather than tampered with:

  ```powershell
  Get-AuthenticodeSignature .\NitroLite-v1.3.2-win-x64.zip | Format-List Status, StatusMessage
  ```

  `Status` reads `NotSigned`. That is expected, and it is the same answer you get for any unsigned open-source
  binary.

If you want Microsoft to look at the file rather than take the author's word for it, submit it through
**[Microsoft Security Intelligence: submit a file](https://www.microsoft.com/en-us/wdsi/filesubmission)**. The
result is public and does not depend on this repository.

The digest only proves the download matches the release; it says nothing about who published it. See
[Trust and code signing](https://github.com/canerztk8/NitroLite/wiki/Trust-and-Code-Signing) for what that does and does not cover.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
