# Releasing CertConvert

A concise runbook for cutting a public release. CertConvert ships as a
self-contained single-file binary per platform, and the in-app updater reads
the GitHub `releases/latest` endpoint, so a few steps below are load-bearing —
get the order wrong and the updater breaks for everyone. Follow it top to bottom.

## Prerequisites

- Docker (for the no-SDK verification gate and the containerised win-x64 publish).
- A host .NET 10 SDK for the macOS publishes — `DOTNET_ROOT="$HOME/.dotnet"` and
  `PATH="$HOME/.dotnet:$PATH"` if it is user-local. Not needed if you pass
  `--docker-win` and only want the Windows artifact.
- `gh` authenticated as `jermainewalkes`, and push access to both remotes
  (`origin` = GitLab, `github` = GitHub).

## 1. Bump the version

Edit `src/CertConvert/CertConvert.csproj` — set both `<Version>` and
`<InformationalVersion>` to the new `X.Y.Z` (semver: breaking.feature.fix).
The updater compares against `InformationalVersion`, so they must match.

## 2. Run the verification gate

```bash
./build-in-docker.sh
```

Runs both test suites and a Release build in the `mcr.microsoft.com/dotnet/sdk:10.0`
Linux container — no host SDK required. It must be green before you go on. The
container writes its obj/bin under `artifacts/docker-build/` (gitignored) so it
never clashes with in-tree host builds.

## 3. Build the artifacts

```bash
build/publish.sh                 # host dotnet for all three RIDs
# or, no Windows toolchain / SDK on PATH:
build/publish.sh --docker-win    # win-x64 via the container, osx-* on the host
```

Produces four files in `artifacts/`:

- `CertConvert-X.Y.Z-osx-x64.zip`
- `CertConvert-X.Y.Z-osx-arm64.zip`
- `CertConvert-X.Y.Z-win-x64.zip`
- `SHA256SUMS.txt`

Do not rename these or change the layout: the updater matches an asset by its
RID substring and a `.zip` suffix, and reads `SHA256SUMS.txt` entries in the
`<hex><two spaces><filename>` form that `shasum -a 256` emits. The zip must keep
`win-x64/CertConvert.exe...` inside it.

## 4. Tag and push to both remotes

```bash
git tag -a vX.Y.Z -m "CertConvert X.Y.Z"
git push origin vX.Y.Z      # GitLab
git push github vX.Y.Z      # GitHub
```

## 5. Publish the GitHub release — release first, assets second

This ordering is not optional. Create the release **published and without
assets**, then upload the four files separately:

```bash
gh release create vX.Y.Z --repo jermainewalkes/certconvert \
  --title "CertConvert X.Y.Z" --notes-file notes.md --latest

gh release upload vX.Y.Z --repo jermainewalkes/certconvert \
  artifacts/CertConvert-X.Y.Z-osx-x64.zip \
  artifacts/CertConvert-X.Y.Z-osx-arm64.zip \
  artifacts/CertConvert-X.Y.Z-win-x64.zip \
  artifacts/SHA256SUMS.txt
```

Why split it: a single `gh release create ... <assets>` that is interrupted
mid-upload leaves the release stuck as an invisible **draft**. Drafts are hidden
from `releases/latest` (it 404s), so the in-app updater silently stops seeing
new versions. Publishing first guarantees the release is live the moment the tag
resolves; the uploads then only add files to an already-visible release. The
updater needs a non-draft release with `SHA256SUMS.txt` attached — without the
checksums file it falls back to TLS-only integrity, and without the platform zip
it reports no build for this platform.

## 6. Smoke check

```bash
curl -s https://api.github.com/repos/jermainewalkes/certconvert/releases/latest \
  | grep '"tag_name"'
```

Must return the new `vX.Y.Z`. If it 404s or shows the previous tag, the release
is still a draft — fix it before announcing, or the updater will not offer it.

## Release notes conventions

Keep notes in the house style — British punctuation, no Oxford comma,
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

## Host builds after a container run

The container writes only into `artifacts/docker-build/` (via `--artifacts-path`),
so a plain host `dotnet build` keeps working with no cleanup. If you ever need a
clean slate, `dotnet clean` or `git clean -xdf artifacts bin obj` clears it.
