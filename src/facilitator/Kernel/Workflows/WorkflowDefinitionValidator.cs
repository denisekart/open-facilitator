using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The definition validator: schema first (the shipped
/// <c>workflow-definition.v1</c> document through the same embedded-schema
/// loader the wire ingresses use), then referential law the schema cannot
/// speak:
/// <list type="bullet">
/// <item>every <c>emit.command</c> is in the fail-closed command table;</item>
/// <item>every trigger/<c>wait.event</c> type exists among the mirrored schema $ids;</item>
/// <item>capability ids match the idToken law;</item>
/// <item>every <c>decide</c> carries <c>fallback</c>; <c>on:</c> edges point at
/// existing step ids; the step graph is acyclic;</item>
/// <item>the invoke-call graph is cycle-free (the same
/// <see cref="WorkflowPreemption"/> static check the runtime uses — one law,
/// one implementation).</item>
/// </list>
/// Palette kinds outside the currently-executable set validate structurally
/// but fail enable-time with <c>step-kind-not-supported</c> (v1 interpreter
/// scope: the interpreter does not grow speculatively).
/// </summary>
public sealed class WorkflowDefinitionValidator(EnvelopeValidator envelopes)
{
    /// <summary>Dataschema $id of the definition artifact.</summary>
    public const string SchemaId = "https://schemas.openfacilitator.org/v1/workflow-definition.v1.json";

    /// <summary>Step kinds the v1 interpreter executes. Growth is deliberate:
    /// each entry lands with its consumer — <c>work-create</c> when
    /// AssignRules imports (Phase 5), <c>invoke</c> with the composition layer
    /// (Phase 6); fan-out/join/memory when their consumer lands. The
    /// interpreter does not grow speculatively.</summary>
    public static IReadOnlySet<string> ExecutableKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "emit", "wait", "timer", "approval", "decide", "select", "fan-out", "fork-work", "work-create", "escalate", "invoke", "signal",
    };

    /// <summary>Validate a parsed definition document (structure + referential
    /// law). Does NOT check enable-time executability — see
    /// <see cref="EnableTimeErrors"/>.</summary>
    public WorkflowValidationResult Validate(JsonElement document)
    {
        var errors = new List<WorkflowValidationError>();
        if (Encoding.UTF8.GetByteCount(document.GetRawText()) > WorkflowDoc.MaxDocumentBytes)
        {
            errors.Add(new("definition-too-large",
                $"document exceeds the {WorkflowDoc.MaxDocumentBytes} byte cap"));
        }

        var structural = envelopes.ValidateAgainstSchema(SchemaId, document);
        if (!structural.Ok)
        {
            errors.Add(new(structural.ErrorClass ?? "schema-violation", structural.Message ?? "schema violation"));
            return new(errors);
        }

        var steps = document.GetProperty("steps").EnumerateArray().ToList();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString()!;
            if (!stepIds.Add(stepId))
            {
                errors.Add(new("duplicate-step-id", $"step id '{stepId}' occurs twice"));
            }
        }

        // Trigger referential law.
        var trigger = document.GetProperty("on");
        if (trigger.TryGetProperty("event", out var triggerEvent))
        {
            RequireEventType(errors, triggerEvent.GetString()!, "trigger");
        }

        if (trigger.TryGetProperty("schedule", out var schedule))
        {
            var spec = schedule.GetString()!;
            if (spec.StartsWith("interval:", StringComparison.Ordinal)
                && !(spec["interval:".Length..].EndsWith('m')
                    && int.TryParse(spec[(("interval:".Length)..^1)], out var minutes) && minutes > 0))
            {
                errors.Add(new("bad-schedule", $"interval cadence '{spec}' is not interval:<n>m with n > 0"));
            }
        }

        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString()!;
            if (step.TryGetProperty("on", out var edge))
            {
                var selector = edge.GetString()!;
                var colon = selector.IndexOf(':');
                if (colon > 0 && !stepIds.Contains(selector[..colon]))
                {
                    errors.Add(new("unknown-edge-target", $"step '{stepId}' selects edge '{selector}': source step does not exist"));
                }
            }

            foreach (var (kind, payload) in Payload(step))
            {
                switch (kind)
                {
                    case "emit":
                        if (payload.TryGetProperty("command", out var command)
                            && !KnownCommands.All.Contains(command.GetString()!))
                        {
                            errors.Add(new("unknown-command",
                                $"step '{stepId}' emits '{command}': not in the v1 command table"));
                        }

                        if (payload.TryGetProperty("binding", out var binding)
                            && binding.TryGetProperty("provider", out var provider)
                            && provider.ValueKind == JsonValueKind.String
                            && provider.GetString()!.StartsWith('$'))
                        {
                            errors.Add(new("binding-provider-templated",
                                $"step '{stepId}' binding provider must be a literal (scoped copies bake it)"));
                        }

                        break;
                    case "wait":
                        RequireEventType(errors, payload.GetProperty("event").GetString()!, $"step '{stepId}' wait");
                        break;
                    case "decide":
                        if (!payload.TryGetProperty("fallback", out var fb))
                        {
                            errors.Add(new("decide-missing-fallback", $"step '{stepId}': decide must carry a fallback branch"));
                        }
                        else
                        {
                            RequireBranch(errors, stepIds, stepId, fb.GetString()!);
                        }

                        if (payload.GetProperty("source").GetString() == "model" && !payload.TryGetProperty("question", out _))
                        {
                            errors.Add(new("decide-missing-question", $"step '{stepId}': source 'model' requires a question"));
                        }
                        else if (payload.GetProperty("source").GetString() == "guard" && !payload.TryGetProperty("guard", out _))
                        {
                            errors.Add(new("decide-missing-guard", $"step '{stepId}': source 'guard' requires a guard"));
                        }
                        else if (payload.TryGetProperty("question", out var question))
                        {
                            foreach (var option in question.GetProperty("options").EnumerateArray())
                            {
                                RequireBranch(errors, stepIds, stepId, option.GetProperty("branch").GetString()!);
                            }
                        }

                        break;
                    case "signal":
                        // The registry law again: the signal rides a wire
                        // event type the mirrored schema set knows (the
                        // sink validates the row's envelope on delivery).
                        RequireEventType(
                            errors,
                            payload.GetProperty("event").GetProperty("type").GetString()!,
                            $"step '{stepId}' signal event");
                        break;
                    case "invoke":
                        // Existence/enabled checks need the store, so the
                        // enable path (which has it) owns them; the static
                        // cycle law runs here against the provided graph only.
                        if (!payload.TryGetProperty("timeout", out _))
                        {
                            errors.Add(new("invoke-missing-timeout", $"step '{stepId}': invoke requires a timeout"));
                        }

                        if (payload.TryGetProperty("onTimeout", out var ot) && !stepIds.Contains(ot.GetString()!))
                        {
                            errors.Add(new("unknown-edge-target", $"step '{stepId}' invoke onTimeout '{ot}' does not exist"));
                        }

                        break;
                }
            }
        }

        // Acyclic step graph: edges step.source -> step (via `on:`); a cycle
        // means no first step is reachable from `always`.
        foreach (var cycle in FindStepCycles(steps, stepIds))
        {
            errors.Add(new("step-cycle", $"step graph cycle: {string.Join(" -> ", cycle)}"));
        }

        return new(errors);
    }

    /// <summary>Enable-time law beyond structure: palette kinds outside the
    /// executable set refuse to enable (422 <c>step-kind-not-supported</c>)
    /// until their consumer lands.</summary>
    public WorkflowValidationResult EnableTimeErrors(JsonElement document)
    {
        var errors = new List<WorkflowValidationError>();
        foreach (var step in document.GetProperty("steps").EnumerateArray())
        {
            var stepId = step.GetProperty("id").GetString()!;
            foreach (var (kind, _) in Payload(step))
            {
                if (!ExecutableKinds.Contains(kind))
                {
                    errors.Add(new("step-kind-not-supported",
                        $"step '{stepId}' uses '{kind}': not executable by the v1 interpreter"));
                }
            }
        }

        return new(errors);
    }

    /// <summary>The (kind, payload) pairs of a step: exactly the one palette key.</summary>
    public static IEnumerable<(string Kind, JsonElement Payload)> Payload(JsonElement step)
    {
        foreach (var property in step.EnumerateObject())
        {
            if (property.Name is not ("id" or "title" or "on") && property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return (property.Name, property.Value);
            }
        }
    }

    private void RequireEventType(List<WorkflowValidationError> errors, string eventType, string where)
    {
        // "org.facilitator.event.provider.review.v1" -> "event.provider.review.v1.json".
        const string prefix = "org.facilitator.";
        var fileName = eventType.StartsWith(prefix, StringComparison.Ordinal)
            ? eventType[prefix.Length..] + ".json"
            : null;
        var schemaId = $"https://schemas.openfacilitator.org/v1/{fileName}";
        if (fileName is null || !envelopes.KnowsSchema(schemaId))
        {
            errors.Add(new("unknown-event-type", $"{where}: event type '{eventType}' has no mirrored schema '{fileName}'"));
        }
    }

    private static void RequireBranch(List<WorkflowValidationError> errors, HashSet<string> stepIds, string stepId, string branch)
    {
        if (branch != "end" && !stepIds.Contains(branch))
        {
            errors.Add(new("unknown-edge-target", $"step '{stepId}' branches to '{branch}': neither a step id nor 'end'"));
        }
    }

    private static IEnumerable<IReadOnlyList<string>> FindStepCycles(
        IReadOnlyList<JsonElement> steps, HashSet<string> stepIds)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString()!;
            if (step.TryGetProperty("on", out var edge))
            {
                var selector = edge.GetString()!;
                var colon = selector.IndexOf(':');
                if (colon > 0)
                {
                    var source = selector[..colon];
                    if (stepIds.Contains(source))
                    {
                        (edges.TryGetValue(source, out var l) ? l : edges[source] = []).Add(stepId);
                    }
                }
            }
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unseen, 1 on stack, 2 done
        var cycles = new List<IReadOnlyList<string>>();
        var stack = new List<string>();

        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in edges.TryGetValue(node, out var list) ? list : [])
            {
                if (state.TryGetValue(next, out var s) && s == 1)
                {
                    var at = stack.IndexOf(next);
                    cycles.Add(stack.GetRange(at, stack.Count - at).Append(next).ToList());
                }
                else if (!state.ContainsKey(next))
                {
                    Visit(next);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString()!;
            if (!state.ContainsKey(stepId))
            {
                Visit(stepId);
            }
        }

        return cycles;
    }
}
