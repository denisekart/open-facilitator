.PHONY: restore build test lint format protocol db-update compose-slim compose-full compose-down clean link-e2e gateway-e2e worker-e2e admin-e2e example-slim-up docs-check provider-e2e

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
	@echo "and the dev IdP at KEYCLOAK_URL (default: http://localhost:8480, see docs/getting-started.md §2);"
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
	@echo "a host Facilitator on :8088 (see examples/slim/.../README.md §1) and"
	@echo "'node features/build.mjs' (Feature runtime tarballs) beforehand."
	npx --yes @devcontainers/cli@^0.79 up --workspace-folder examples/slim/deterministic-research-worker

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

# Provider-ingest live proof (Plan 22 + 23 + 24): the full stack must be up
# first (`make compose-full`); 20 steps (plan 35 Phase C adds provision-cold-start,
# provision-kill9), verify-shaped report.
# Step selection: make provider-e2e ARGS="--only provider-boot".
provider-e2e:
	@echo "Provider E2E tier: needs the FULL stack (make compose-full) — mattermost :8065, adapter :8066, kernel :8088."
	REF="$(CURDIR)/reference" bash reference/checks/verify-full.sh $(ARGS)

compose-down:
	cd reference && docker compose --env-file versions.env --env-file .env \
	-f compose.slim.yaml -f compose.provisioning.yaml $(if $(FULL),-f compose.full.yaml,) down -v
	@# Kernel-provisioned containers (elastic fleet) are NOT compose services —
	@# the launcher's law keeps them alive; teardown removes them by label first.
	@docker ps -aq --filter label=io.facilitator.managed | xargs -r docker rm -f

clean:
	dotnet clean OpenFacilitator.slnx
	rm -rf src/*/dist node_modules
