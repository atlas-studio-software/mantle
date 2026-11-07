using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features;

/// <summary>One checked option value: the value to keep, a reset (null), or why it is refused.</summary>
public readonly record struct OptionCheck(JsonElement Value, bool Reset, string? Reason)
{
    public bool Ok => Reason == null;
}

/// <summary>
/// lib/features.js checkOption / validateOptions / normChoices, PURE: what an option value may be,
/// with the refusal in the words Mantle Settings shows ("&lt;label&gt;: must be at least 1 s").
/// </summary>
public static class FeatureOptions
{
    public const int TextMax = 500;
    static readonly Regex KeyRe = new("^[a-zA-Z][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);
    static readonly Regex FullPath = new(@"^([a-zA-Z]:\\|\\\\)", RegexOptions.CultureInvariant);

    /// A plain value as a detached JSON element (bool, string, a number, or null).
    public static JsonElement ToElement(object? v) => v switch
    {
        null => Parse("null"),
        bool b => Parse(b ? "true" : "false"),
        string s => Parse(JsonEncode(s)),
        double d => Parse(d.ToString("R", CultureInfo.InvariantCulture)),
        float f => Parse(((double)f).ToString("R", CultureInfo.InvariantCulture)),
        int i => Parse(i.ToString(CultureInfo.InvariantCulture)),
        long l => Parse(l.ToString(CultureInfo.InvariantCulture)),
        JsonElement e => e.Clone(),
        _ => throw new ArgumentException($"an option value must be true/false, text, a number or null, not {v.GetType().Name}"),
    };

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static string JsonEncode(string s) => JsonSerializer.Serialize(s, OptionJson.Default.String);

    static string Unit(FeatureOption o) => string.IsNullOrEmpty(o.Unit) ? "" : " " + o.Unit;

    static string Num(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    static string ChoiceValueText(FeatureChoice c) => c.Value;

    /// <summary>Is <paramref name="v"/> a valid value for <paramref name="opt"/>? JSON null = back to the default.</summary>
    public static OptionCheck Check(FeatureOption opt, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return new(default, true, null);
        switch (opt.Kind)
        {
            case FeatureOptionKind.Bool:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? new(v.Clone(), false, null) : Refuse($"{opt.Label}: must be on or off");
            case FeatureOptionKind.Choice:
            {
                var text = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                var hit = text == null ? null : opt.Choices.FirstOrDefault(c => string.Equals(ChoiceValueText(c), text, StringComparison.Ordinal));
                return hit != null ? new(v.Clone(), false, null)
                    : Refuse($"{opt.Label}: must be one of {string.Join(", ", opt.Choices.Select(c => c.Label))}");
            }
            case FeatureOptionKind.Number:
            {
                double n = double.NaN;
                if (v.ValueKind == JsonValueKind.Number) n = v.GetDouble();
                else if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    double.TryParse(v.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
                if (!double.IsFinite(n)) return Refuse($"{opt.Label}: must be a number");
                if (opt.Min is double min && n < min) return Refuse($"{opt.Label}: must be at least {Num(min)}{Unit(opt)}");
                if (opt.Max is double max && n > max) return Refuse($"{opt.Label}: must be at most {Num(max)}{Unit(opt)}");
                return new(ToElement(n), false, null);
            }
            case FeatureOptionKind.Text:
                if (v.ValueKind != JsonValueKind.String) return Refuse($"{opt.Label}: must be text");
                if (v.GetString()!.Length > TextMax) return Refuse($"{opt.Label}: is longer than {TextMax} characters");
                return new(v.Clone(), false, null);
            case FeatureOptionKind.Folder:
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(s)) return Refuse($"{opt.Label}: must be a folder");
                if (!FullPath.IsMatch(s)) return Refuse($"{opt.Label}: must be a full folder path (like C:\\Users\\...)");
                return new(v.Clone(), false, null);
            }
            default:
                return Refuse($"{opt.Label}: has an unknown type");
        }
    }

    static OptionCheck Refuse(string why) => new(default, false, why);

    /// <summary>Null, or the plain-words reason a feature's options list breaks the contract.</summary>
    public static string? Validate(IReadOnlyList<FeatureOption>? list)
    {
        if (list == null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var opt in list)
        {
            if (opt == null) return "every option must be an object";
            if (opt.Key == null || !KeyRe.IsMatch(opt.Key)) return $"option key \"{opt.Key}\" is not a plain word";
            if (!seen.Add(opt.Key)) return $"option \"{opt.Key}\" is listed twice";
            if (string.IsNullOrWhiteSpace(opt.Label)) return $"option \"{opt.Key}\" has no label";
            if (!Enum.IsDefined(opt.Kind)) return $"option \"{opt.Key}\": type must be one of bool, choice, number, text, folder";
            if (opt.Kind == FeatureOptionKind.Choice && opt.Choices.Count == 0) return $"option \"{opt.Key}\": a choice needs choices";
            if (opt.Kind == FeatureOptionKind.Number && new[] { opt.Min, opt.Max, opt.Step }.Any(n => n is double d && !double.IsFinite(d)))
                return $"option \"{opt.Key}\": min, max and step must be numbers";
            var nullable = opt.Kind is FeatureOptionKind.Text or FeatureOptionKind.Folder;
            if (opt.Default == null && !nullable) return $"option \"{opt.Key}\" has no default";
            if (opt.Default != null)
            {
                JsonElement d;
                try { d = ToElement(opt.Default); } catch (ArgumentException e) { return $"option \"{opt.Key}\": its default is not valid ({e.Message})"; }
                var c = Check(opt, d);
                if (!c.Ok) return $"option \"{opt.Key}\": its default is not valid ({c.Reason})";
            }
        }
        return null;
    }

    /// The kind's wire name, as Mantle Settings and features.list say it.
    public static string KindName(FeatureOptionKind k) => k switch
    {
        FeatureOptionKind.Bool => "bool",
        FeatureOptionKind.Choice => "choice",
        FeatureOptionKind.Number => "number",
        FeatureOptionKind.Text => "text",
        FeatureOptionKind.Folder => "folder",
        _ => "unknown",
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class OptionJson : System.Text.Json.Serialization.JsonSerializerContext { }
