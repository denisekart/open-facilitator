# syntax=docker/dockerfile:1
# NON-PRODUCTION REFERENCE image (the reference/container/Dockerfile.elastic
# pattern): a pinned node runtime base, the built dist + its production
# closure only, non-root, outbound-only (no EXPOSE — the launcher dials the
# kernel's link plane, nothing dials in).
#
# Build CONTEXT = the repo root (the pnpm workspace closure), e.g. from the
# root: docker build -f examples/aws/fleet-launcher/Dockerfile .
FROM node:22-bookworm-slim AS build
WORKDIR /repo
RUN corepack enable
# Manifest layer first: installs only need the workspace topology.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.base.json ./
COPY src/protocol/package.json src/protocol/
COPY src/workspace-link/package.json src/workspace-link/
COPY examples/aws/fleet-launcher/package.json examples/aws/fleet-launcher/
RUN pnpm install --frozen-lockfile
COPY src/protocol/ src/protocol/
COPY src/workspace-link/ src/workspace-link/
COPY examples/aws/fleet-launcher/ examples/aws/fleet-launcher/
RUN pnpm --filter @ofac/protocol build \
 && pnpm --filter @ofac/workspace-link build \
 && pnpm --filter example-fleet-launcher build
# --prod + injected workspace deps: a runnable closure (node_modules with the
# workspace packages copied in), freshly built dist/ included via `files`.
RUN pnpm deploy --filter=example-fleet-launcher --prod /deploy/fleet-launcher

# Debian slim, not Alpine: better-sqlite3 (the link ledger) ships glibc
# prebuilds (pnpm-workspace.yaml allowBuilds note).
FROM node:22-bookworm-slim AS runtime
# A fixed minor: the fleet launcher is reference code; the fleet task
# definition pins this image by digest (supply-chain law: digests, not tags).
COPY --from=build /deploy/fleet-launcher /app
# Writable state root for the SQLite link ledger (an EFS access point mounts
# over this path in TeamFleetStack).
RUN install -d -o node -g node /var/lib/ofac
USER node
ENV OFAC_STATE_PATH=/var/lib/ofac/fleet-launcher.sqlite3
ENTRYPOINT ["node", "/app/dist/bin/launcher.js"]
