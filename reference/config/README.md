# Reference service configuration

Runtime config files for reference-infra services, consumed as mounted inputs.

- `facilitator.json` — the Facilitator kernel's Production configuration,
  mounted read-only at `/app/config/facilitator.json` by `compose.slim.yaml`
  and loaded by the kernel's mounted-config provider (path overridable via
  env `Config__File`). Carries the identity block (OIDC authority, claim
  descriptors, roles, authorization bindings) and the Link roster; secrets
  (connection string, S3 credentials, enrollment token) arrive only via
  `.env`-interpolated environment variables. Spec §22.1 sketches this file as
  `facilitator.yaml`; net10.0 ships no YAML configuration provider (the
  Microsoft `…Configuration.Yaml*` packages never left preview), so the
  artifact is JSON.
