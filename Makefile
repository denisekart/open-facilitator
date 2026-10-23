.PHONY: restore build test lint format compose-slim compose-full clean

restore:
	dotnet restore OpenFacilitator.slnx
	pnpm install

build: restore
	dotnet build OpenFacilitator.slnx -c Release --no-restore
	pnpm -r build

test: build
	dotnet test OpenFacilitator.slnx -c Release --no-build
	pnpm -r test

lint:
	dotnet format OpenFacilitator.slnx --verify-no-changes
	pnpm lint

format:
	dotnet format OpenFacilitator.slnx
	pnpm format

compose-slim compose-full:
	@echo "Reference environment lands in Plan 11: cd reference && ./referencectl up slim|full"
	@exit 2

clean:
	dotnet clean OpenFacilitator.slnx
	rm -rf src/*/dist node_modules
