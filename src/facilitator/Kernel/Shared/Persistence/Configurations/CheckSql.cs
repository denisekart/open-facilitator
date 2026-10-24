namespace Facilitator.Kernel.Shared.Persistence.Configurations;

/// <summary>CHECK-constraint SQL shared by every entity configuration (verbatim form).</summary>
internal static class CheckSql
{
    /// <summary><c>column IN ('a', 'b', ...)</c> — the exact CHECK expression every table uses.</summary>
    public static string InList(string column, IEnumerable<string> allowed) =>
        $"{column} IN ({string.Join(", ", allowed.Select(static v => $"'{v}'"))})";
}
