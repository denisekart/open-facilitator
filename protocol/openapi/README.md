# OpenAPI 3.1 contract

HTTP surface of the Facilitator (Link fallback + admin API). `link-v1.yaml` is the
implemented Link fallback stub; `admin-v1.yaml` is the hand-kept mirror
of the `/api/admin` operator surface — kernel-API contract, not wire
protocol, so it carries no JSON-Schema `$ref`s and no conformance fixtures; the
runtime tests under `tests/Facilitator.Tests/Admin/` are its drift check. Both
files are validated by `make protocol` and the CI protocol job. Implementation
slices keep them in lockstep with the code.
