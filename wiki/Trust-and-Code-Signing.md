# Trust and code signing

**NitroLite is not code-signed.** A Windows code-signing certificate that SmartScreen accepts costs a few hundred
dollars a year and needs a registered business, which this project does not have. So the exe carries no publisher
signature, and Windows shows "Windows protected your PC" the first time you run it. That warning is about the missing
signature, not about anything found in the file.

What you can check instead, without trusting the author:

| Check | How |
|---|---|
| The download came from this repository | The link on the Releases page points at `github.com/canerztk8/NitroLite`; HTTPS to GitHub is the transport. |
| The binary was built in public | Every release is built by [GitHub Actions](https://github.com/canerztk8/NitroLite/blob/main/.github/workflows/release.yml) on a `windows-latest` runner from the tag, not on the author's laptop. The workflow run is linked from the release page, and its log shows the test run and the packaging step. |
| The file is the one that was published | [`Get-FileHash`](https://github.com/canerztk8/NitroLite#verify-the-download) and compare it with the digest on the release. |
| The source matches the binary | Build it yourself from the tag; `scripts\publish-release.ps1` is the same script the workflow runs. |
| The updater does not take a shortcut | It compares the downloaded package's SHA-256 against the digest GitHub publishes and refuses to swap the exe on a mismatch. |

The honest limit: if this GitHub account were taken over, an attacker could publish a release and its digest together,
and the digest check would pass. Two-factor authentication on the account is what actually protects you, not the
signature. If you need a signed binary, build from source and sign it with your own certificate.

---

[< Back to Home](https://github.com/canerztk8/NitroLite/wiki)
