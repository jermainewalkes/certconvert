# Releasing CertConvert

A concise runbook for cutting a release. CertConvert is published on the **Mac
App Store** and the **Microsoft Store**; those two packages are the release.
GitLab (`origin`) holds the release commit and tag. **GitHub is deliberately kept
one version behind the Stores** (see step 6), so nothing in a release is pushed
there. Follow it top to bottom.

## Prerequisites

- Docker (for the verification gate).
- A host .NET 10 SDK for the macOS build — `DOTNET_ROOT="$HOME/.dotnet"` and
  `PATH="$HOME/.dotnet:$PATH"` if it is user-local. The Store builds are
  self-contained, so **the SDK decides which runtime patch ships**: install the
  newest .NET 10 SDK first (for example SDK 10.0.401, which carries runtime
  10.0.12), and re-pin the Docker image in `build-in-docker.sh` to the digest
  that carries the same SDK. `dotnet --list-runtimes` shows what an SDK brings.
- The Windows build VM (`ssh winbuild`) for the Microsoft Store package.
- The Apple signing identities and provisioning profile described under
  [Mac App Store](#mac-app-store-buildmake-mas-pkgsh).

## 1. Bump the version

Edit `src/CertConvert/CertConvert.csproj` — set both `<Version>` and
`<InformationalVersion>` to the new `X.Y.Z` (semver: breaking.feature.fix).
The updater compares against `InformationalVersion`, so they must match.

Then update the places that state the version or describe the release:

- `site/changelog.html` — the new entry, and the range in its meta description.
- `site/index.html` and `site/download.html` — `softwareVersion`, and the
  "Latest version" row on the download page.
- `design/mac-app-store-listing.md` and `design/microsoft-store-listing.md` —
  package file names and the "What's New" text.

The site describes what a customer can get today, so **deploy the site only
once the Stores have approved the release** (`site/deploy-site.sh`). Site copy
about a command-line feature waits longer still: the command line exists only
in source builds, and the public source is one version behind (step 6).

## 2. Run the verification gate

```bash
./build-in-docker.sh
```

Runs both test suites plus **two** Release builds — the normal variant and the
store variant (`-p:StoreBuild=true`) — in the .NET 10 SDK Linux container
(pinned by digest in `build-in-docker.sh`; bump deliberately, not by drift).
No host SDK required. It must be green before you go on. The container writes
its obj/bin under `artifacts/docker-build/` (gitignored) so it never clashes
with in-tree host builds.

## 3. Commit, then build the Store packages

Commit the release first. `certconvert --version` reports the git commit it was
built from, so the packages must be built from the committed release, with a
clean working tree.

```bash
build/make-mas-pkg.sh        # → artifacts/mas/CertConvert-X.Y.Z.pkg   (this Mac)
./build-on-windows.sh        # → artifacts/msix/CertConvert-X.Y.Z.msix (Windows VM)
```

Details and prerequisites for each are in the [Mac App Store](#mac-app-store-buildmake-mas-pkgsh)
and [Microsoft Store](#microsoft-store-buildmake-msixps1) sections below.

## 4. Check what was built

Before uploading, confirm each package is what the release claims:

- The Mac package is signed by the installer identity:
  `pkgutil --check-signature artifacts/mas/CertConvert-X.Y.Z.pkg`.
- Both bundle the intended runtime patch. The restored runtime pack version is
  recorded in `artifacts/mas/int/obj/CertConvert/project.assets.json` and in
  `artifacts/msix/layout/CertConvert.deps.json`.
- The MSIX manifest carries version `X.Y.Z.0` and the unchanged Store identity
  (`artifacts/msix/layout/AppxManifest.xml`).

## 5. Tag, push to GitLab and upload to the Stores

```bash
git tag -a vX.Y.Z -m "CertConvert X.Y.Z"
git push origin main vX.Y.Z      # GitLab only
```

Upload the `.pkg` to App Store Connect and the `.msix` to Partner Center, using
the paste-ready text in `design/mac-app-store-listing.md` and
`design/microsoft-store-listing.md`. Uploading does not submit: attaching the
build and submitting for review is a separate, deliberate step.

## 6. GitHub: one version behind

GitHub (`github` remote, `jermainewalkes/certconvert`) is the free, public
source. It is kept **one released version behind the Stores**, so Store
customers get each release first. When version N is published on the Stores:

- Do **not** push N to GitHub.
- GitHub may now receive N-1. Push that release's commit, never the tip of
  `main`:

  ```bash
  PREV=v1.3.1        # set this: the tag of the release BEFORE the one just published
  git push github "$PREV^{commit}:refs/heads/main"
  git push github "$PREV"
  ```

  Every release from 1.3.1 on is tagged on GitLab (`git tag --sort=-v:refname`
  lists them). At the 1.4.0 release there was nothing to push: GitHub already
  held 1.3.1.

GitHub pushes need Jermaine's go-ahead each time.

## 7. After the Stores approve: deploy the site

Once both Stores show the new version as live, publish the site changes made in
step 1, so the download page and changelog match what customers can get:

```bash
site/deploy-site.sh
```

### If a GitHub release with downloads is ever published

Build the downloads from the release being published to GitHub, not from the
tip of `main`: `git checkout "$PREV"` first. The in-app updater of a source build reads the GitHub `releases/latest`
endpoint, so the order below is load-bearing. `build/publish.sh` produces
`CertConvert-X.Y.Z-osx-x64.zip`, `-osx-arm64.zip`, `-win-x64.zip` and
`SHA256SUMS.txt` in `artifacts/`. Do not rename them or change the layout: the
updater matches an asset by its RID substring and a `.zip` suffix, and reads
`SHA256SUMS.txt` in the `<hex><two spaces><filename>` form `shasum -a 256` emits.

Create the release **published and without assets**, then upload the files
separately:

```bash
gh release create vX.Y.Z --repo jermainewalkes/certconvert \
  --title "CertConvert X.Y.Z" --notes-file notes.md --latest

gh release upload vX.Y.Z --repo jermainewalkes/certconvert \
  artifacts/CertConvert-X.Y.Z-osx-x64.zip \
  artifacts/CertConvert-X.Y.Z-osx-arm64.zip \
  artifacts/CertConvert-X.Y.Z-win-x64.zip \
  artifacts/SHA256SUMS.txt
```

A single `gh release create ... <assets>` that is interrupted mid-upload leaves
the release stuck as an invisible **draft**. Drafts are hidden from
`releases/latest` (it 404s), so the updater silently stops seeing new versions.
Check afterwards that the endpoint returns the new tag:

```bash
curl -s https://api.github.com/repos/jermainewalkes/certconvert/releases/latest \
  | grep '"tag_name"'
```

## Release notes conventions

For a GitHub release with downloads (see above). Keep notes in the house style — British punctuation, no Oxford comma,
sentence-case headings, no AI attribution. Include:

- A short summary of what changed.
- A **downloads** table mapping platform to asset:

  | Platform            | Download                            |
  | ------------------- | ----------------------------------- |
  | macOS (Apple)       | `CertConvert-X.Y.Z-osx-arm64.zip`   |
  | macOS (Intel)       | `CertConvert-X.Y.Z-osx-x64.zip`     |
  | Windows             | `CertConvert-X.Y.Z-win-x64.zip`     |

- **Unsigned build, first launch** steps while builds are unsigned:
  - macOS: unzip, drag `CertConvert.app` to Applications, then right-click →
    Open (or clear quarantine with `xattr -dr com.apple.quarantine CertConvert.app`).
  - Windows: unzip, run `CertConvert.exe`, and on SmartScreen choose
    More info → Run anyway.
- A **SHA256** section pointing at `SHA256SUMS.txt` and how to verify
  (`shasum -a 256 -c SHA256SUMS.txt`).
- A Ko-fi link: https://ko-fi.com/jwalkes

## Mac App Store (build/make-mas-pkg.sh)

Store builds are a separate, sandboxed variant — `-p:StoreBuild=true` strips the
self-updater and Ko-fi links (the store handles updates; buyers have already
paid). `build/make-mas-pkg.sh` produces `artifacts/mas/CertConvert-<version>.pkg`,
signed and ready for App Store Connect.

The recipe is fiddly for a reason — see the header comment in the script. Key
facts: Avalonia under App Sandbox only launches when published **single-file**
(otherwise it aborts with no LaunchServices ASN), but plain single-file
**self-extracts** its native dylibs and the sandbox then flags the app
"damaged" — so we publish single-file with
`IncludeNativeLibrariesForSelfExtract=false` and sign the three native dylibs in
place. This recipe is reusable for any Avalonia Mac App Store app.

Prerequisites (one-off, in the Apple Developer portal): App ID
`com.certconvert.app` (no capabilities needed — sandbox is an entitlement, not a
capability); Apple Distribution + Mac Installer Distribution certs (with their
private keys, and the current WWDR G3 intermediate — an expired WWDR breaks the
chain silently); a **Mac App Store** provisioning profile saved at
`design/CertConvert_Mac_App_Store.provisionprofile`. The profile is
**gitignored** (account-specific, expires yearly): regenerate it in the portal
under Profiles → + → Distribution → Mac App Store Connect, select the App ID
and the Apple Distribution cert, download and save to that path. The script
strips extended attributes from the whole bundle before signing — a
browser-downloaded profile carries `com.apple.quarantine`, which Apple rejects
after upload (ITMS-91109).

```bash
build/make-mas-pkg.sh          # → artifacts/mas/CertConvert-<version>.pkg
```

`CFBundleVersion` (the build number) defaults to the marketing version. Apple
requires it **unique and higher on every upload** and at most three
period-separated integers, so to re-upload the same marketing version, bump
only the build number:

```bash
MAS_BUILD=1.1.2 build/make-mas-pkg.sh   # marketing stays <version>, build 1.1.2
```

Then create the app record in App Store Connect (name, bundle id
`com.certconvert.app`), upload the `.pkg` via Transporter or the App Store
Connect API, and test through TestFlight (a profile-embedded build cannot be
launched locally by design — "launchd job spawn failed" — TestFlight is the
correct test surface). Currently arm64-only; universal (Intel) is deferred.

## Microsoft Store (build/make-msix.ps1)

Runs on Windows (needs the Windows SDK's `MakeAppx.exe`) — the Fusion VM or a
GitHub Actions `windows-latest` runner. Produces
`artifacts/msix/CertConvert-<version>.msix`, an **unsigned** store package (the
Store re-signs on submission — do not sign it).

```powershell
pwsh build/make-msix.ps1
```

It publishes the store variant (`-p:StoreBuild=true`, self-contained win-x64),
lays the payload plus `build/msix/assets` and `build/msix/AppxManifest.xml` into
a package layout, stamps the manifest version to `<version>.0` (the Store needs
a 4-part version with revision 0), and packs the `.msix`.

The manifest identity is fixed to the Partner Center values (Product Identity
page) and must not change: `Name=JermaineWalkes.CertConvert`,
`Publisher=CN=2F7BAB54-DD4D-405B-A67E-4539A9971265`,
`PublisherDisplayName=Jermaine Walkes` (Store ID `9NT6HCG0JBFV`). Tile assets
come from `build/make-icons.py --msix`. Upload at Partner Center → CertConvert →
Submissions → Packages.

## Host builds after a container run

The container writes only into `artifacts/docker-build/` (via `--artifacts-path`),
so a plain host `dotnet build` keeps working with no cleanup. If you ever need a
clean slate, `dotnet clean` or `git clean -xdf artifacts bin obj` clears it.
