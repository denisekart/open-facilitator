# docs/workflows.md reference page

## Context
Authority files re-read: WorkflowEndpoints.cs (routes), WorkflowDefinitionValidator.cs (ExecutableKinds ~L39, EnableTimeErrors ~L226), protocol/json-schema/workflow-definition.v1.json ($defs/trigger oneOf, 15 palette kinds), Gallery/*.json (8), WorkflowGallerySeeder.cs, WorkflowRunner.cs/WorkflowSweeper.cs (park rows), site/build.mjs workflowsAppendix() lifecycle.

## Goal
Create docs/workflows.md: first heading `# The workflow plane`; immutable versions + toggle states; triggers; two-tier step-kind law; one mermaid stateDiagram lifecycle; operator controls; gallery table; durability law; "Where to go next" with relative docs links.

## Files
| File | Owner | Action |
|------|-------|--------|
| docs/workflows.md | orchestrator | create |

## Acceptance
- [ ] Facts match sources (toggle route, trigger oneOf, 15-schema/13-executed, join/memory -> step-kind-not-supported at enable)
- [ ] One mermaid stateDiagram-v2 titled fence matching build.mjs lifecycle
- [ ] 8-row gallery table
- [ ] "Where to go next" relative links to protocol.md, overview.md, security.md

## Verification
Re-grep doc against sources; confirm no absolute docs links.
