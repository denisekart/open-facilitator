using System.Text.Json;

namespace Facilitator.Kernel.Link;

/// <summary>Capability-manifest gate for POST /capabilities (schema + shape).</summary>
static class CapabilityManifestValidator
{
    public static bool Validate(JsonElement manifest, out string? violation)
    {
        violation = null;
        if (manifest.ValueKind != JsonValueKind.Object)
        {
            violation = "manifest must be a JSON object";
            return false;
        }

        if (!manifest.TryGetProperty("ownerTeam", out var team) || team.ValueKind != JsonValueKind.String)
        {
            violation = "manifest.ownerTeam is required";
            return false;
        }

        if (!manifest.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array)
        {
            violation = "manifest.capabilities must be an array";
            return false;
        }

        return true;
    }
}
