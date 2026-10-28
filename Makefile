.PHONY: restore build test lint format protocol db-update compose-slim compose-full clean link-e2e gateway-e2e worker-e2e example-slim-up docs-check

restore:
	dotnet restore OpenFacilitator.slnx
	pnpm install

build: restore
	dotnet build OpenFacilitator.slnx -c Release --no-restore
	pnpm -r build

test: build
	dotnet test OpenFacilitator.slnx -c Release --no-build
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
	git diff --exit-code -- src/protocol/src/generated protocol/dotnet/Ofac.Protocol/Generated src/protocol/schemas

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
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml up -d --build
	@echo "Full loop (seed + wait + demo workspace): ./referencectl up slim — this target is the raw compose path."
	@echo "Facilitator: http://localhost:8088/health · Keycloak: http://localhost:8080 (realm facilitator, admin demo-admin)"

compose-full:
	cd reference && cp -n .env.example .env && \
	docker compose --env-file versions.env --env-file .env -f compose.slim.yaml -f compose.full.yaml up -d --build
	@echo "Slim stack + forgejo :3000, mattermost :8065, kafka :9092, ollama :11434 (loopback)"

compose-down:
	cd reference && docker compose --env-file versions.env --env-file .env \
	-f compose.slim.yaml $(if $(FULL),-f compose.full.yaml,) down -v

clean:
	dotnet clean OpenFacilitator.slnx
	rm -rf src/*/dist node_modules
