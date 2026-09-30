using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CarriolaConverter;

public sealed record UserOptions
{
    public string Format { get; init; } = "webp";
    public uint Quality { get; init; } = 85;
    public string Folder { get; init; } = "";
    public bool Rename { get; init; } = true;
    public bool OpenFolder { get; init; }
    public bool Notify { get; init; } = true;
    public bool IncludeSubfolders { get; init; }
    public ResizeOptions Resize { get; init; } = new();
    public string Background { get; init; } = "#FFFFFF";
}

public sealed record NamedProfile(Guid Id, string Name, UserOptions Options);
public sealed record AppPreferences
{
    public int Version { get; init; } = 1;
    public UserOptions Last { get; init; } = new();
    public List<NamedProfile> Profiles { get; init; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppPreferences))]
internal partial class PreferencesJsonContext : JsonSerializerContext { }
