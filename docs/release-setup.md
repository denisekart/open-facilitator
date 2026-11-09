# Release artifactory setup

Operator runbook for provisioning the registries and Actions credentials this
repo's deployment pipeline consumes. Targets: `release.yml` (tag-triggered:
`containers`, `features`, `publish-npm`, plus the sbom + digest-pin jobs) and
`ci.yml` (merge gate; consumes `reference/versions.env` keys). Every secret and
variable name below appears verbatim in the workflow files.

```mermaid
flowchart LR
    TAG[push v* tag] --> C[containers job]
    TAG --> F[features job]
    TAG --> N[publish-npm job<br/>if vars.ENABLE_NPM_PUBLISH]
    TAG --> S[sbom + digest-pin jobs<br/>(pipeline target)]
    C -->|GITHUB_TOKEN packages:write| GHCR1[ghcr.io/denisekart/open-facilitator/facilitator<br/>ghcr.io/denisekart/open-facilitator/node-runtime]
    F -->|GITHUB_TOKEN packages:write| GHCR2[ghcr.io/denisekart/open-facilitator/features/&lt;name&gt;]
    N -->|OIDC trusted publisher| NPM[registry.npmjs.org @ofac]
    S -->|gh release upload| REL[GitHub Release assets]
    S -->|commit back / PR| VEN[reference/versions.env *_DIGEST]
    GHCR1 -.pinned by.-> VEN
```

## 1. Account layout and the `open-facilitator` GHCR namespace

The repo lives at `github.com/denisekart/open-facilitator` (see the
`repository.url` of every publishable package). GitHub Packages npm is the one
artifact family whose owner constraint reaches into the account layout — decide
section 3's scope question before creating anything else.

1. Confirm the account: `gh api users/denisekart --jq .type` —
   `User` or `Organization`. GHCR and GitHub Packages work under both; no org
   is required for OCI or for a NuGet feed.
2. Namespace contract: the pipeline pushes everything under
   `ghcr.io/denisekart/open-facilitator/`. `GITHUB_TOKEN` can create and push
   packages only in the namespace owned by the repository's owner, so a fork
   under a different owner cannot reuse these coordinates as-is — either move
   the repo under `denisekart` or rewrite `OCI_REGISTRY`
   (`reference/versions.env`) and the tag lists in `release.yml` together.
3. Package auto-creation: first push with `packages: write` creates each
   container package. Pre-creating in the UI (your profile/org → Packages →
   "Create a repository" is not needed; just let the first tag push create
   them) is OPTIONAL. What is NOT optional: package visibility. A container
   package created by a workflow links to the repo, but if the push lands
   before the link resolves, or the account defaults new packages to private,
   anonymous consumers 401. After the first release, verify each package is
   public:
   ```sh
   gh api user/packages/container/open-facilitator-facilitator --jq .visibility
   gh api user/packages/container/open-facilitator-node-runtime --jq .visibility
   # features publish creates one package per feature + collection metadata:
   gh api user/packages/container/open-facilitator-features-gateway --jq .visibility
   ```
   (GHCR names each package as the image path's trailing components joined with
   `-`: `ghcr.io/denisekart/open-facilitator/features/gateway` → package
   `open-facilitator-features-gateway`.) Flip to public under
   the package page → Package settings → "Change visibility", or
   `PATCH /user/packages/container/{name}/versions`-adjacent visibility API.
4. Permission model: no PAT for pushes. The `containers`, `features`, and
   `publish-npm` jobs run with per-job `permissions: { contents: read,
   packages: write }` and authenticate as `${{ github.actor }}` /
   `secrets.GITHUB_TOKEN`. Keep repo Settings → Actions → General →
   "Workflows permissions: Read and write allowed" (the default); do not set
   the org/repo limit to Read-only.
5. Feature OCI path contract (consumers depend on this —
   `docs/getting-started.md` (registry coordinates table) and `examples/slim/deterministic-research-worker/README.md`
   advertise the coordinates): `@devcontainers/cli features publish` pushes
   each directory of `features/src` to
   `<registry>/<namespace>/<id>` where `<id>` is the `id` field of that
   directory's `devcontainer-feature.json`, NOT the folder name. The published
   coordinate consumers use is `ghcr.io/denisekart/open-facilitator/features/<name>`.
   Therefore each manifest's `id` MUST equal its folder name
   (`gateway`, `workspace-link`, `harness-adapter`, `forgejo-provider`).
   Verify against the manifests before the first tag:
   ```sh
   grep -H '"id"' features/src/*/devcontainer-feature.json
   ```
   If the manifests carry `ofac-`-prefixed ids, the publish lands under
   `…/features/ofac-gateway` and both the `tag majors` step and every consumer
   coordinate 404 — rename the ids (or the consumer coordinates) first.

## 2. OCI feature registry (GHCR)

The `features` job builds self-contained payload tarballs (`node
features/build.mjs`), then runs
`npx --yes @devcontainers/cli@^0.79 features publish` against `features/src`.
CLI semantics (`@devcontainers/cli` 0.79):

- Flags: `--registry` takes ONLY the registry host (`ghcr.io`, the default);
  `--namespace <owner>/<repo>` (required) supplies the collection path. A
  publish line must therefore be
  `--registry ghcr.io --namespace denisekart/open-facilitator/features features/src`
  — the registry as one slash-including token with no namespace argument
  fails argument validation and lands the wrong repo path. Confirm the job's
  publish line passes these as two flags before the first tag.
- Auth: the job's `docker/login-action` with
  `username: ${{ github.actor }}`, `password: secrets.GITHUB_TOKEN` writes
  docker config credentials; the CLI reads them, so publishing needs no extra
  secret. Already-published exact versions are skipped; the CLI auto-publishes
  semantic tags (`1.0.0` → also `1.0`, `1`, `latest` when it is the max
  published for that range) plus collection metadata (the index behind
  `ghcr.io/denisekart/open-facilitator/features`).
- The `tag majors` step (`docker buildx imagetools create -t …:1 …:1.0.0`)
  re-asserts the major tag; it rides the same docker login — no additional
  creds. Its feature list must cover every published feature (today:
  `gateway workspace-link harness-adapter`; `forgejo-provider` exists under
  `features/src` and needs adding to that loop before it can be consumed by
  major tag).
- Manual first walkthrough from a workstation:
  ```sh
  echo "<pat-with-packages:write>" | docker login ghcr.io -u denisekart --password-stdin
  node features/build.mjs
  npx --yes @devcontainers/cli@^0.79 features publish \
    --registry ghcr.io --namespace denisekart/open-facilitator/features features/src
  ```
- Consumer reference form (any team's `devcontainer.json`):
  ```jsonc
  "features": {
    "ghcr.io/denisekart/open-facilitator/features/gateway:1": {
      "configPath": ".facilitator/gateway.yaml"
    }
  }
  ```
  Public packages resolve anonymously (devcontainers CLI needs no login);
  private packages make `devcontainer up` dial GHCR with your local
  `docker login ghcr.io` creds.

Verification after any release:

```sh
docker buildx imagetools inspect ghcr.io/denisekart/open-facilitator/features/gateway:1
# expect a manifest whose annotations carry dev.containers.metadata
```

## 3. npm

All workspace packages are scoped `@ofac/*` (`@ofac/protocol`,
`@ofac/gateway`, `@ofac/workspace-link`, …); every one is `"private": true`
until it is intentionally shipped. The chosen route is **npmjs.org** (the
old GitHub Packages option died on the scope-vs-owner rule: `@ofac` cannot
publish from a `denisekart`-owned repo — see the options below). Publishable
packages carry `publishConfig.registry: https://registry.npmjs.org` and the
repo-root `.npmrc` routes the scope there:

```
@ofac:registry=https://registry.npmjs.org
```

The `publish-npm` job is filled and gated on the repository variable
`ENABLE_NPM_PUBLISH=true`: it stamps the tag's version into every package
that flipped `"private": false`, builds them, and publishes the workspace
subset with `pnpm publish -r --provenance --access public` (OIDC trusted
publisher — no registry token). One-time account work is in
**Route c — npmjs.org** below.

**Route chosen — c: npmjs.org** (the GitHub Packages option is blocked
here anyway: the scope must match the publishing repo's owner, and
`@ofac/*` from a `denisekart`-owned repo is rejected — a future switch
needs an `ofac`-owned repo or a scope rename). One-time account work, in
order:

1. Create the organization `ofac` on npmjs.com (Account settings →
   Organizations → "Create organization"). The org owns the `@ofac` scope;
   your personal account joins as owner.
2. Pre-create each scoped package name so it lands under the org: on
   npmjs.com, the org page → Packages/Add package → "Scoped Package"
   (e.g. `@ofac/protocol`), visibility Public. (A first
   `npm publish --access public` also creates it under the publishing
   identity, but pre-creating avoids a personal-scope squat.)
3. Register the trusted publisher per package (tokenless OIDC, no
   long-lived npm token): package page on npmjs.com → "Publishing from
   GitHub Actions" → Trusted Publisher → provider GitHub Actions;
   organization `denisekart`, repository `open-facilitator`, workflow
   name `release.yml`. Repeat per package (or set it org-wide).
4. Flip the job on:
   `gh api -X PUT repos/denisekart/open-facilitator/actions/variables/ENABLE_NPM_PUBLISH -f name=ENABLE_NPM_PUBLISH -f value=true`
5. Flip the packages intended for consumption to `"private": false`
   (first: `@ofac/protocol`). The job stamps the tag's version at publish
   time — no version bump in the tree.

The job publishes with `id-token: write` +
`pnpm publish -r --provenance --access public`: provenance attestations
ride the Actions OIDC token, and consumers see
`npm audit signatures` evidence. Consumer side is zero-config — public
scoped packages resolve on the default registry, no `.npmrc` and no PAT
(unlike GitHub Packages, which has no anonymous read). That consumer ease
is what route (c) buys.

Verification (after a publish):

```sh
npm view @ofac/protocol version                # resolves on the default registry
npm view @ofac/protocol dist.attestations      # provenance attached
```

## 4. NuGet

Status today: nothing ships to NuGet. The one packable library is
`protocol/dotnet/Ofac.Protocol/Ofac.Protocol.csproj` (plain
`Microsoft.NET.Sdk` classlib; `IsPackable` is unset, so `dotnet pack` works —
it just has no `Version`/`RepositoryUrl`/readme metadata yet; the two test
projects set `IsPackable=false`, the kernel is a Web SDK app). `Ofac.Protocol`
is therefore a future seam with concrete steps:

1. Metadata: add `<Version>`, `<PackageId>Ofac.Protocol</PackageId>`,
   `<RepositoryUrl>`, `<Description>` to the csproj (or CPM-side properties).
   Prove the pack locally without publishing:
   `dotnet pack protocol/dotnet/Ofac.Protocol -c Release -o /tmp/nupkg && ls /tmp/nupkg`
2. Pick a feed:
   - **nuget.org** — register an org/prefix on nuget.org (prefix `ofac.` if
     you want the namespace; `Ofac.Protocol` itself needs no prefix
     reservation). Store an API key as Actions secret `NUGET_API_KEY` (repo →
     Settings → Secrets and variables → Actions → Secrets). Publish step:
     `dotnet nuget push /tmp/nupkg/*.nupkg -k $NUGET_API_KEY -s https://api.nuget.org/v3/index.json --skip-duplicate`.
   - **GitHub Packages NuGet feed** — no setup beyond `packages: write` (the
     feed auto-exists per owner): push source
     `https://nuget.pkg.github.com/denisekart/index.json`, username = the
     token's owner, password = `secrets.GITHUB_TOKEN`. Consumers add the
     source with a PAT and reference the lowercased id (`ofac.protocol`).
3. Gate it the same way as npm: a repo variable (e.g. `ENABLE_NUGET_PUSH`)
   behind an `if:` on a new `publish-nuget` job — nothing today.

Verification once live:
`dotnet package search Ofac.Protocol --source https://api.nuget.org/v3/index.json`
(or `gh api user/packages/nuget/ofac.protocol` for GH Packages).

## 5. Actions secrets and variables inventory

Exhaustive list of what the full pipeline consumes (verified by grepping
`secrets.` / `vars.` across `.github/workflows/`):

| Name | Kind | Where consumed | Purpose | Needed today? |
|---|---|---|---|---|
| `GITHUB_TOKEN` | implicit secret | `release.yml` `containers`, `features` | GHCR push (`docker/login-action`) | Yes — job perms `contents: read`, `packages: write` |
| `ENABLE_NPM_PUBLISH` | repo variable | `release.yml` `publish-npm` `if:` | activates npm publishing | Set to `true` only after section 3's npmjs.org account work (org, names, trusted publisher) lands |
| `NUGET_API_KEY` | secret (optional) | future `publish-nuget` job | nuget.org push | Only for the nuget.org route |
| `REPO_ADMIN_PAT` (name at your choice) | secret (optional) | future digest-pin job | fine-grained PAT, single repo, `contents: write` (+ `metadata: read`), to commit `*_DIGEST` updates back when branch protection or PR-triggered re-verification requires non-`GITHUB_TOKEN` authorship | Only if digest-pin commits directly to a protected branch |

Job permission notes:

- All current `release.yml` jobs declare per-job `permissions`; the workflow
  has no top-level `permissions:` block. A `GITHUB_TOKEN` push to `ghcr.io`
  needs `packages: write` — present on all three jobs.
- The digest-pin job (pipeline target, section 6) additionally needs
  `contents: write` to commit back to `reference/versions.env`, either via
  `GITHUB_TOKEN` (its commit does not re-trigger workflows, which is fine for
  a `[skip ci]`-style pin commit) or via the fine-grained PAT secret.
- `ci.yml` references no named secrets or variables; it rides the default
  `GITHUB_TOKEN` (`contents: read`).
- No Actions environments are used; nothing to provision there.

Verify the inventory from the CLI:

```sh
gh api repos/denisekart/open-facilitator/actions/variables --jq '.[].name'
gh api repos/denisekart/open-facilitator/actions/secrets --jq '.secrets[].name'
```

## 6. Digest pinning (`reference/versions.env`)

`reference/versions.env` carries the pin keys: `POSTGRES_DIGEST`,
`KEYCLOAK_DIGEST`, `KAFKA_DIGEST`, `FORGEJO_DIGEST`, `MATTERMOST_DIGEST`,
`SEAWEEDFS_DIGEST`, `ASPIRE_DIGEST`, `OLLAMA_DIGEST`, `FACILITATOR_DIGEST`
(all empty by hand-edit policy — `reference/AGENTS.md`: "Empty `*_DIGEST`
entries are filled by the release process, not by hand"; the digest-pin job is
expected to add a `RUNTIME_DIGEST` for `node-runtime`). Compose brings images
up today via `${OCI_REGISTRY}/…:${FRAMEWORK_TAG}` interpolation; once pins are
non-empty the pin-of-record for a release tag is verified as:

```sh
docker buildx imagetools inspect \
  ghcr.io/denisekart/open-facilitator/facilitator:v1.2.3 \
  --format '{{json .Manifest.Digest}}'   # must equal FACILITATOR_DIGEST's sha256:…
# third-party pin:
docker buildx imagetools inspect postgres:17-alpine --format '{{json .Manifest.Digest}}'
# compose still honors a digest-pinned reference (image: …@sha256:…) identically:
./referencectl up slim --no-seed && ./referencectl verify
```

The `digest-pin` job (pipeline target) resolves digests of the images it just
pushed — plus the third-party tags, so upstream retagging can't move a
reference env under a release — and commits/PRs them back to
`reference/versions.env`. `ci.yml`'s `meta` job currently enforces key
presence; per the milestone it flips to enforcing non-empty values, so the
first real release must land all pins in one commit or CI goes red. Verify
locally:

```sh
grep -E '^[A-Z_]+_DIGEST=.+$' reference/versions.env   # all non-empty after first release
```

## 7. SBOM

The sbom job (pipeline target) builds a CycloneDX SBOM per artifact — npm side
(`cyclonedx-npm` over the workspace, covers
gateway/workspace-link/harness payloads), .NET side (CycloneDX tool over
`OpenFacilitator.slnx`) — and attaches them to the GitHub Release created for
the `v*` tag (`gh release upload <tag> … --clobber`). The GitHub Release itself
is created by the tag push tooling (`softprops/action-gh-release` or
`gh release create` in that job); no release exists until then.

Verification:

```sh
gh release view v1.2.3 --json assets --jq '.assets[].name'
# expect per-artifact SBOM assets, e.g. facilitator.cdx.json, node-runtime.cdx.json
```

## 8. Smoke-test a fresh setup without burning a real version

Ordered end-to-end proof. Uses a throwaway `v0.0.0-smoketest` tag (any `v*`
triggers the workflow); delete it after.

```sh
# 0. preconditions
git status --porcelain                        # clean tree
gh auth status                                # repo scope on denisekart/open-facilitator
grep -H '"id"' features/src/*/devcontainer-feature.json   # ids == folder names (section 1.5)

# 1. fire the pipeline
git tag v0.0.0-smoketest && git push origin v0.0.0-smoketest
gh run list --workflow release.yml --limit 1
gh run watch <run-id>                         # containers + features green

# 2. images: pull both arches by digest, anonymously (public visibility proof)
docker buildx imagetools inspect ghcr.io/denisekart/open-facilitator/facilitator:v0.0.0-smoketest
docker buildx imagetools inspect ghcr.io/denisekart/open-facilitator/node-runtime:v0.0.0-smoketest
D=$(docker buildx imagetools inspect \
      ghcr.io/denisekart/open-facilitator/facilitator:v0.0.0-smoketest \
      --format '{{json .Manifest.Digest}}' | tr -d '"')
docker pull ghcr.io/denisekart/open-facilitator/facilitator@$D

# 3. features: major tag + real install in a scratch devcontainer
docker buildx imagetools inspect ghcr.io/denisekart/open-facilitator/features/gateway:1
mkdir -p /tmp/ofac-scratch/.devcontainer
cat > /tmp/ofac-scratch/.devcontainer/devcontainer.json <<'EOF'
{
  "image": "mcr.microsoft.com/devcontainers/typescript-node:1-22-bookworm",
  "overrideCommand": false,
  "features": {
    "ghcr.io/denisekart/open-facilitator/features/gateway:1": {
      "configPath": ".facilitator/gateway.yaml"
    }
  }
}
EOF
npx --yes @devcontainers/cli@^0.79 up --workspace-folder /tmp/ofac-scratch
npx --yes @devcontainers/cli@^0.79 exec --workspace-folder /tmp/ofac-scratch \
  sh -c 'command -v ofac-gateway'             # PATH shim from the published layer
npx --yes @devcontainers/cli@^0.79 down --workspace-folder /tmp/ofac-scratch

# 4. npm (only if ENABLE_NPM_PUBLISH=true and a package flipped public):
npm view @ofac/protocol version
npm view @ofac/protocol dist.attestations   # provenance ride the OIDC publish

# 5. SBOM + release assets
gh release view v0.0.0-smoketest --json assets

# 6. pin round-trip (after the digest-pin job has committed)
grep '^FACILITATOR_DIGEST=sha256' reference/versions.env
./referencectl up slim --no-seed && ./referencectl verify   # 11/11 honors the pins

# 7. teardown of the smoke release (packages may stay)
gh release delete v0.0.0-smoketest --cleanup-tag --yes
```

## Gap checklist before the first real tag

Derived from reading the pipeline files as they stand; each item is a one-line
change owned by the workflow/features code, not by this runbook's registry
setup:

| # | Gap | Fix owner | Section |
|---|---|---|---|
| 1 | ~~Feature `id` fields (`ofac-*`) differ from folder names~~ **closed**: every manifest's `id` now equals its folder name | — | 1.5, 2 |
| 2 | ~~`features publish` line passed the path as one `--registry` token~~ **closed**: now `--registry ghcr.io --namespace denisekart/open-facilitator/features` | — | 2 |
| 3 | ~~`tag majors` loop omitted `forgejo-provider`~~ **closed**: loop covers all six features | — | 2 |
| 4 | ~~npm scope `@ofac` vs GitHub Packages owner rule~~ **closed**: route (c) npmjs.org, org `ofac`, OIDC trusted publisher | — | 3 |
| 5 | ~~digest-pin lacked `contents: write`~~ **closed**: both `sbom` and `digest-pin` declare `contents: write` (a PAT is only needed if branch protection requires PRs) | — | 5, 6 |

Cross-references: `.github/workflows/README.md` (job-by-job scope),
`docs/getting-started.md` (registry coordinates table),
`features/AGENTS.md` (install contract), `reference/AGENTS.md` (versions.env
law).
