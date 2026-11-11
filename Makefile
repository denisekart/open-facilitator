.PHONY: restore build test lint format protocol db-update compose-slim compose-full compose-down clean link-e2e gateway-e2e worker-e2e admin-e2e example-slim-up docs-check provider-e2e gitlab-up gitlab-down example-local-smoke scenario-gate scenario-walks scenario-golden scenario-plane-up scenario-gate-e2e ref-env release release-npm

restore:
	dotnet restore OpenFacilitator.slnx
	pnpm install

build: restore
	# SPA first: the kernel build stages src/facilitator/wwwroot (vite output).
	pnpm -r build
	dotnet build OpenFacilitator.slnx -c Release --no-restore

test: build
	# Kernel suite only: the browser tier is `make admin-e2e` (provisioned separately).
	dotnet test tests/Facilitator.Tests/Facilitator.Tests.csproj -c Release --no-build
	pnpm -r test
	dotnet run --project protocol/tools/Validate.Fixtures -c Release --no-build

lint:
	dotnet format OpenFacilitator.slnx --verify-no-changes
	pnpm lint

format:
	dotnet format OpenFacilitator.slnx
	pnpm format

docs-check:
	node protocol/tools/agents-docs-check.mjs

protocol:
	pnpm protocol:codegen
	npx swagger-cli validate protocol/openapi/link-v1.yaml
	npx swagger-cli validate protocol/openapi/admin-v1.yaml
	git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated src/protocol/schemas


admin-e2e: build
	@echo "Browser tier: kernel SPA against Kestrel + Testcontainers (Docker needed);"
	@echo "chromium downloads to ~/.cache/ms-playwright on first run."
	node tests/Facilitator.E2E/bin/Release/net10.0/.playwright/package/cli.js install chromium
	dotnet test tests/Facilitator.E2E/Facilitator.E2E.csproj -c Release --no-build
link-e2e:
	@echo "E2E tier: needs Postgres at FACILITATOR_PG (default: facilitator-dev on 5433)"
	@echo "and the dev IdP at KEYCLOAK_URL (default: http://localhost:8480, see docs/getting-started.md section 2);"
	@echo "no psql binary on PATH? PG_ADMIN_CMD='docker exec facilitator-dev psql' PG_ADMIN_URL=postgresql://postgres:postgres@localhost:5432/postgres"
	FACILITATOR_URL=$${FACILITATOR_URL:-http://gate-only} \
	FACILITATOR_PG=$${FACILITATOR_PG:-postgresql://postgres:postgres@127.0.0.1:5433/postgres} \
	KEYCLOAK_URL=$${KEYCLOAK_URL:-http://localhost:8480} \
	pnpm -C src/workspace-link test:e2e

gateway-e2e:
	@echo "Gateway E2E tier: needs Postgres at FACILITATOR_PG (default: facilitator-dev on 5433);"
	@echo "no psql binary on PATH? PG_ADMIN_CMD='docker exec facilitator-dev psql' PG_ADMIN_URL=postgresql://postgres:postgres@localhost:5432/postgres"
	FACILITATOR_URL=$${FACILITATOR_URL:-http://gate-only} \
	FACILITATOR_PG=$${FACILITATOR_PG:-postgresql://postgres:postgres@127.0.0.1:5433/postgres} \
	KEYCLOAK_URL=$${KEYCLOAK_URL:-http://localhost:8480} \
	pnpm -C src/gateway test:e2e

worker-e2e:
	@echo "Harness-worker E2E tier: standalone Gateway + real ofac-deterministic-worker binary;"
	@echo "same Postgres gate as gateway-e2e."
	FACILITATOR_URL=$${FACILITATOR_URL:-http://gate-only} \
	FACILITATOR_PG=$${FACILITATOR_PG:-postgresql://postgres:postgres@127.0.0.1:5433/postgres} \
	KEYCLOAK_URL=$${KEYCLOAK_URL:-http://localhost:8480} \
	pnpm -C src/adapters/harnesses/deterministic test:e2e

example-slim-up:
	@echo "Slim example devcontainer: needs Docker, @devcontainers/cli (npx @devcontainers/cli),"
	@echo "a host Facilitator on :8088 (see examples/slim/.../README.md section 1) and"
	@echo "'node features/build.mjs' (Feature runtime tarballs) beforehand."
	npx --yes @devcontainers/cli@^0.79 up --workspace-folder examples/slim/deterministic-research-worker

example-local-smoke:
	@echo "Local home-deployment example gate: AI-free sim run (needs Docker Engine;"
	@echo "NOT wired into make test — same posture as the AWS example)."
	pnpm -C examples/local/tools/localctl build
	examples/local/localctl init --env sim
	examples/local/localctl smoke --env sim --clean-exit

db-update:
	dotnet tool restore
	dotnet ef database update -p src/facilitator -s src/facilitator

compose-slim:
	cd reference && cp -n .env.example .env && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml build demo-workspace && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml --profile build-only build elastic-worker && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml up -d --build
	@echo "Full loop (seed + wait + demo workspace): ./referencectl up slim — this target is the raw compose path."
	@echo "Facilitator: http://localhost:8088/health · Keycloak: http://localhost:8080 (realm facilitator, admin demo-admin)"

compose-full:
	cd reference && cp -n .env.example .env && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml -f compose.full.yaml build demo-workspace && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml -f compose.full.yaml --profile build-only build elastic-worker && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.provisioning.yaml -f compose.full.yaml up -d --build --wait --wait-timeout 600
	@echo "Slim stack + forgejo :3000, mattermost :8065, kafka :9092, ollama :11434 (loopback)"
	@echo "Provider adapter :8066 (GET /healthz), kernel provider section ON. Live proof: make provider-e2e"

# Provider-ingest live proof (Mattermost + Forgejo + coding-worker + provisioning
# legs): the full stack must be up first (`make compose-full`); verify-shaped
# report over every provider and provisioning leg.
# Step selection: make provider-e2e ARGS="--only provider-boot".
provider-e2e:
	@echo "Provider E2E tier: needs the FULL stack (make compose-full) — mattermost :8065, adapter :8066, kernel :8088."
	REF="$(CURDIR)/reference" bash reference/checks/verify-full.sh $(ARGS)
	@echo "GitLab is on-demand, not in this set: make gitlab-up && ./reference/checks/verify-gitlab.sh"

# --- Scenario gate (docs/scenarios/approved-scenarios.md) --------------------
# The audit record's cross-check source: point TRX at a fuller run's trx to
# cross-check more of the manifest (the full-suite `make test` writes one).
TRX ?= tests/Facilitator.Tests/TestResults/scenario-gate.trx
WALK_TRX ?= tests/Facilitator.ScenarioWalks/TestResults/scenario-walks.trx

# The composed scenario walks: cross-feature sequences over their own
# collection (one container pair, driven clock, no fixed sleeps).
scenario-walks: build
	dotnet test tests/Facilitator.ScenarioWalks/Facilitator.ScenarioWalks.csproj -c Release --no-build \
	  --blame-hang-timeout 2m --logger "trx;LogFileName=scenario-walks.trx"

# The T1+T3 lane: the xUnit wall proves the reviewed bindings manifest
# (evidence ids, catalog coverage). `--dotnet-trx` cross-checks verdicts
# against the tests a run actually observed (point it at the full-suite trx
# for the audit record's full cross-check; a filtered run proves only its
# slice), and the runner writes the audit rows. When a walk run's trx exists
# it folds in as a second cross-check source, so walk-bound rows are judged
# on what the walk run actually observed.
scenario-gate: build
	dotnet test tests/Facilitator.Tests/Facilitator.Tests.csproj -c Release --no-build \
	  --filter "FullyQualifiedName~ScenarioGate" \
	  --logger "trx;LogFileName=scenario-gate.trx" \
	&& node tools/scenario-plane/src/runner.mjs \
	   --dotnet-trx $(TRX) $(if $(wildcard $(WALK_TRX)),--dotnet-trx $(WALK_TRX),) --md \
	   > docs/scenarios/audit-results.md

# The plane alone: a fake provider with a personality on :8791 (control
# surface /__plane/*), for driving adapter legs by hand.
scenario-plane-up:
	node tools/scenario-plane/src/serve.mjs

# The T2 lane: adapter outbound legs over real HTTP against the plane.
scenario-gate-e2e: build
	pnpm -C tools/scenario-plane test:e2e

# The manual golden thread (owner ruling: not CI-wired): the acceptance
# chain against a developer-running kernel (`make compose-slim`), plus the
# in-process fake provider. Fails loud when :8088 is down.
scenario-golden:
	pnpm -C tools/scenario-plane test:golden

# The demoted infra legs (compose slim/full + referencectl verify): manual,
# recorded in docs/runbooks/manual-infra-plan.md - this target only prints
# what a run must cover and the commands.
ref-env:
	@echo "Manual infra plan (see docs/runbooks/manual-infra-plan.md):"
	@echo "  1. make compose-slim && reference/checks/verify.sh legs"
	@echo "  2. make compose-full && provider-e2e legs (verify-full.sh, verify-gitlab.sh)"
	@echo "  3. link-e2e + worker-e2e + admin-e2e browser/container tiers"
	@echo "  4. clean tree: up slim --no-seed && referencectl verify"

# --- On-demand GitLab CE (arm64-capable omnibus image, off by default) --------
# Boots GitLab CE loopback on :8929 with a minimal omnibus profile
# (prometheus/grafana/registry/alertmanager off — the arm64 WSL memory
# contingency), waits for the web UI, then runs the idempotent API
# bootstrap (root PAT minted in-container — recent GitLab refuses password
# app-auth; bot user + PAT, org/project from tools/gitlab/seed/, work
# label, sample issue, project webhook -> the kernel sink, local-target
# allowlist on). The kernel must carry an enabled Providers__Gitlab__
# section for the legs to PASS (see
# src/adapters/communication/gitlab/README.md for the example lines).
gitlab-up:
	docker rm -f facilitator-gitlab 2>/dev/null || true
	docker run -d --name facilitator-gitlab --shm-size=256m --memory=6g \
	  -p 127.0.0.1:8929:8929 -p 127.0.0.1:8922:22 \
	  -v ofac-gitlab-config:/etc/gitlab -v ofac-gitlab-logs:/var/log/gitlab \
	  -v ofac-gitlab-data:/var/opt/gitlab \
	  -e GITLAB_ROOT_PASSWORD="$(or $(GITLAB_ROOT_PASSWORD),0fac-Rt7Vm4Xq2Lz)" \
	  -e GITLAB_OMNIBUS_CONFIG="external_url 'http://127.0.0.1:8929'; gitlab_rails['gitlab_shell_ssh_port']=8922; prometheus['enable']=false; alertmanager['enable']=false; registry['enable']=false; puma['worker_processes']=2; sidekiq['max_concurrency']=8" \
	  docker.io/gitlab/gitlab-ce:latest
	GITLAB_URL=http://127.0.0.1:8929 \
	  GITLAB_ROOT_PASSWORD="$(or $(GITLAB_ROOT_PASSWORD),0fac-Rt7Vm4Xq2Lz)" \
	  GITLAB_WEBHOOK_TOKEN="$(or $(GITLAB_WEBHOOK_TOKEN),ofac-hook-token)" \
	  GITLAB_TOKEN_FILE="$(CURDIR)/.ofac-artifacts/gitlab-bot-token" \
	  GITLAB_CONTAINER=facilitator-gitlab \
	  bash tools/gitlab/init-gitlab.sh
	@echo "GitLab CE on http://127.0.0.1:8929 (root pw 0fac-Rt7Vm4Xq2Lz). Legs: ./reference/checks/verify-gitlab.sh — teardown: make gitlab-down"

gitlab-down:
	docker rm -f facilitator-gitlab 2>/dev/null || true
	docker volume rm ofac-gitlab-config ofac-gitlab-logs ofac-gitlab-data 2>/dev/null || true
	@echo "GitLab CE container + volumes removed."

compose-down:
	cd reference && docker compose --env-file versions.env --env-file .env \
	-f compose.slim.yaml -f compose.provisioning.yaml $(if $(FULL),-f compose.full.yaml,) down -v
	@# Kernel-provisioned containers (elastic fleet) are NOT compose services —
	@# the launcher's law keeps them alive; teardown removes them by label first.
	@docker ps -aq --filter label=io.facilitator.managed | xargs -r docker rm -f

clean:
	dotnet clean OpenFacilitator.slnx
	rm -rf src/*/dist node_modules

# --- Local release --------------------------------------------------------------
# A release is a v-tag: the pushed tag drives the Release workflow (container
# images, GitHub release + SBOM attachments, digest pins — and the npm publish
# when the ENABLE_NPM_PUBLISH repo variable is on).
#
# `make release` runs the npm half on this machine over the SAME publishable
# closure the workflow uses (tools/npm/publishable-packages.mjs, dependency
# order first): stamp every package to VERSION, build, publish, then restore
# the stamps — the tag points at an unstamped commit, exactly what CI checks
# out. Secrets: an npm login (~/.npmrc) or NPM_TOKEN (one-off root .npmrc);
# provenance stays a CI/OIDC artifact — the local half is a plain token
# publish. Registry versions that already exist are skipped, so a half-failed
# run resumes, and CI's publish-npm (if the gate is on) won't fight a local
# release.
#
#   VERSION=x.y.z make release       # npm publish + tag + push the tag
#   VERSION=x.y.z make release-npm   # the npm half only
#   DRY_RUN=1 …                      # rehearse: no publish, no tag
VERSION ?=
DRY_RUN ?=
NPM_PUBLISH_FLAGS = --no-git-checks --access public $(if $(DRY_RUN),--dry-run,)

release-npm:
	@test -n "$(VERSION)" || { echo "usage: make release-npm VERSION=x.y.z [DRY_RUN=1]"; exit 2; }
	@set -eu; \
	dirs=$$(node tools/npm/publishable-packages.mjs); \
	npmrc=""; \
	if [ -n "$${NPM_TOKEN:-}" ] && [ ! -f .npmrc ]; then \
	  npmrc=.npmrc; printf '//registry.npmjs.org/:_authToken=%s\n' "$$NPM_TOKEN" > .npmrc; \
	fi; \
	restore() { \
	  for d in $$dirs; do git restore -- "$$d/package.json" 2>/dev/null || true; done; \
	  [ -z "$$npmrc" ] || rm -f "$$npmrc"; \
	}; \
	trap restore EXIT; \
	for d in $$dirs; do npm pkg set version="$(VERSION)" -C "$$d"; done; \
	for d in $$dirs; do \
	  name=$$(node -p "require('./$$d/package.json').name"); \
	  if [ -z "$(DRY_RUN)" ] && [ "$$(npm view "$$name@$(VERSION)" version 2>/dev/null || true)" = "$(VERSION)" ]; then \
	    echo "skip $$name@$(VERSION): already on the registry"; \
	    continue; \
	  fi; \
	  pnpm --dir "$$d" build; \
	  echo "publish $$name@$(VERSION)"; \
	  pnpm --dir "$$d" publish $(NPM_PUBLISH_FLAGS); \
	done

release:
	@test -n "$(VERSION)" || { echo "usage: make release VERSION=x.y.z [DRY_RUN=1]"; exit 2; }
	@echo "$(VERSION)" | grep -Eq '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$$' || { echo "VERSION must be semver (x.y.z)"; exit 2; }
	@test -z "$$(git status --porcelain)" || { echo "release from a clean working tree"; exit 2; }
	@test "$$(git rev-parse --abbrev-ref HEAD)" = main || { echo "release from main"; exit 2; }
	@test -z "$$(git ls-remote origin refs/tags/v$(VERSION))" || { echo "v$(VERSION) is already pushed"; exit 2; }
	$(MAKE) --no-print-directory release-npm VERSION=$(VERSION) $(if $(DRY_RUN),DRY_RUN=1,)
	@if [ -z "$(DRY_RUN)" ]; then \
	  git tag -a "v$(VERSION)" -m "open-facilitator v$(VERSION)"; \
	  git push origin "v$(VERSION)"; \
	  echo "v$(VERSION) pushed — the Release workflow now builds containers, SBOMs and digest pins"; \
	else \
	  echo "DRY_RUN: skipped tagging/pushing v$(VERSION)"; \
	fi
