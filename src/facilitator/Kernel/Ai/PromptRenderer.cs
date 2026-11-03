using System.Collections.Concurrent;
using System.Reflection;

using Scriban;
using Scriban.Runtime;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// Sandboxed Scriban renderer for AiDeck prompt templates. Resolution order
/// per template id: <see cref="AiOptions.TemplateDirectory"/> override (when
/// set and the file exists), else the embedded resource
/// <c>Facilitator.Ai.Templates.&lt;templateId&gt;.scriban</c>. Unknown ids
/// throw — a missing template is a composition bug, not a runtime degrade.
/// <para>
/// Split convention: a template may contain a line whose whole content is
/// <c>---USER---</c>; text before it renders as the system prompt, text after
/// it as the user message. Templates without the separator (reply-composer,
/// decision-*) render as a single system prompt with an empty user part.
/// </para>
/// <para>
/// Sandbox law: templates are untrusted data (directory overrides can arrive
/// from disk), so renders expose ONLY readable model properties — no C#
/// methods, no host functions, no indexers. Members arrive under the default
/// Scriban import naming: PascalCase model properties surface as snake_case
/// (e.g. <c>BodyHint</c> → <c>body_hint</c>).
/// </para>
/// </summary>
public sealed class PromptRenderer
{
    /// <summary>Line that splits a template into system/user halves.</summary>
    public const string UserSeparatorLine = "---USER---";

    /// <summary>Manifest-resource prefix for embedded templates (RootNamespace is <c>Facilitator</c>).</summary>
    private const string ResourcePrefix = "Facilitator.Ai.Templates.";

    private readonly AiOptions options;

    /// <summary>Parsed halves per template id (parsed once, rendered many times).</summary>
    private readonly ConcurrentDictionary<string, Compiled> compiled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bind to the resolved <c>Ai:</c> options (template directory override lives here).</summary>
    /// <param name="options">Loaded AiDeck options; never null.</param>
    public PromptRenderer(AiOptions options)
    {
        this.options = options;
    }

    /// <summary>
    /// Render a template to a single text (whole file, system and user
    /// halves joined by a newline when the template carries the separator).
    /// For split templates that need two chat messages, use the out-param
    /// overload instead.
    /// </summary>
    /// <param name="templateId">Template file name without <c>.scriban</c>.</param>
    /// <param name="model">Plain data object (records / anonymous objects); only readable properties resolve.</param>
    /// <returns>The rendered prompt text.</returns>
    /// <exception cref="InvalidOperationException">Unknown template id.</exception>
    public string Render(string templateId, object model)
    {
        var entry = Resolve(templateId);
        var system = RenderHalf(entry.System, model);
        return entry.User is null
            ? system
            : system + "\n" + RenderHalf(entry.User, model);
    }

    /// <summary>Render under the single-text convention (alias of <see cref="Render(string, object)"/>).</summary>
    /// <param name="templateId">Template file name without <c>.scriban</c>.</param>
    /// <param name="model">Plain data object.</param>
    /// <returns>The rendered prompt text.</returns>
    /// <exception cref="InvalidOperationException">Unknown template id.</exception>
    public string RenderFull(string templateId, object model) => Render(templateId, model);

    /// <summary>
    /// Render honoring the <c>---USER---</c> split: the halves become two
    /// prompts (system + user). Templates without the separator yield the
    /// whole render as <paramref name="system"/> and an empty
    /// <paramref name="user"/>, so single-text templates stay usable here.
    /// </summary>
    /// <param name="templateId">Template file name without <c>.scriban</c>.</param>
    /// <param name="model">Plain data object; only readable properties resolve.</param>
    /// <param name="system">Rendered system prompt.</param>
    /// <param name="user">Rendered user prompt (empty when the template is single-text).</param>
    /// <exception cref="InvalidOperationException">Unknown template id.</exception>
    public void Render(string templateId, object model, out string system, out string user)
    {
        var entry = Resolve(templateId);
        system = RenderHalf(entry.System, model);
        user = entry.User is null ? string.Empty : RenderHalf(entry.User, model);
    }

    private Compiled Resolve(string templateId) =>
        compiled.GetOrAdd(templateId, Compile);

    private Compiled Compile(string templateId)
    {
        if (templateId.Length == 0
            || templateId.IndexOf('/') >= 0
            || templateId.Contains('\\'))
        {
            throw new InvalidOperationException($"Unknown prompt template '{templateId}' (bad id).");
        }

        var source = LoadSource(templateId, out var origin);
        var (systemText, userText) = Split(source);
        try
        {
            return new Compiled(Parse(systemText, templateId, origin), userText is null ? null : Parse(userText, templateId, origin));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
    }

    private string LoadSource(string templateId, out string origin)
    {
        var directory = options.TemplateDirectory;
        if (!string.IsNullOrEmpty(directory))
        {
            var path = Path.Combine(directory, templateId + ".scriban");
            if (File.Exists(path))
            {
                origin = path;
                return File.ReadAllText(path);
            }
        }

        var name = ResourcePrefix + templateId + ".scriban";
        var stream = typeof(PromptRenderer).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Unknown prompt template '{templateId}': no file under Ai:TemplateDirectory and no embedded resource '{name}'.");
        }

        origin = "embedded:" + name;
        using (stream)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    private static Template Parse(string text, string templateId, string origin)
    {
        var template = Template.Parse(text, origin);
        if (template.HasErrors)
        {
            throw new InvalidOperationException(
                $"Prompt template '{templateId}' ({origin}) failed to parse: {template.Messages}");
        }

        return template;
    }

    private static (string System, string? User) Split(string source)
    {
        var lines = source.ReplaceLineEndings().Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == UserSeparatorLine)
            {
                return (
                    string.Join("\n", lines[..i]),
                    string.Join("\n", lines[(i + 1)..]));
            }
        }

        return (source, null);
    }

    private static string RenderHalf(Template template, object model)
    {
        // TemplateContext + Import run the template as pure data: the member
        // filter admits only readable properties (methods and indexers never
        // resolve, so no C# host call is reachable), and relaxed member
        // access makes an absent property render empty instead of throwing —
        // a prompt must not fail on missing optional facts.
        var scope = new ScriptObject();
        scope.Import(model, ScriptMemberImportFlags.Property, IsDataProperty, renamer: null);
        var context = new TemplateContext
        {
            MemberFilter = IsDataProperty,
            EnableRelaxedMemberAccess = true,
        };
        context.PushGlobal(scope);
        return template.Render(context);
    }

    private static bool IsDataProperty(MemberInfo member) =>
        member is PropertyInfo property && property.CanRead && property.GetIndexParameters().Length == 0;

    private sealed record Compiled(Template System, Template? User);
}
