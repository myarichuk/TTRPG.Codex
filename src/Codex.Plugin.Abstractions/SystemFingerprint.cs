using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// What a system was when it was loaded (overhaul section 4, rule 8): the version system.yaml states, and a hash of each file
/// its rules come from: system.yaml, its translations, each script and each DLL beside it. A campaign keeps the one it last
/// played under (<c>CampaignDocument.SystemStamp</c>) and the app says what changed when the loaded one differs. Content packs
/// are not in it: they are content, not rules, and a campaign's entries are copied into it when used.
/// </summary>
/// <param name="Version">system.yaml's <c>version</c>, or a compiled plugin's assembly version.</param>
/// <param name="Files">Each file by its name relative to the system's folder, with <c>/</c> between folders, and its hash.</param>
public sealed record SystemFingerprint(string Version, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The hash of a file's bytes: SHA-256, lower-case hex. A text file's line endings are made <c>\n</c> first, so a
    /// checkout on Windows and one on Linux of the same commit are the same system.</summary>
    public static string Hash(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (IsText(path)) bytes = WithoutCarriageReturns(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>The hash of text already read, as <see cref="Hash(string)"/> gives for a file holding it.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).ToLowerInvariant();

    private static bool IsText(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" or ".js" or ".json" or ".md" or ".txt";

    private static byte[] WithoutCarriageReturns(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0) return bytes;
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n') continue;
            output.Add(bytes[i]);
        }

        return output.ToArray();
    }

    /// <summary>What differs from <paramref name="before"/> to this one: the version, and each file added, removed or changed.
    /// Empty when they are the same system.</summary>
    public IReadOnlyList<SystemChange> ChangesSince(SystemFingerprint before)
    {
        var changes = new List<SystemChange>();
        if (!string.Equals(before.Version, Version, StringComparison.Ordinal))
        {
            changes.Add(new SystemChange(SystemChangeKind.Version, null, before.Version, Version));
        }

        foreach (var (file, hash) in Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!before.Files.TryGetValue(file, out var was)) changes.Add(new SystemChange(SystemChangeKind.Added, file, null, hash));
            else if (!string.Equals(was, hash, StringComparison.Ordinal)) changes.Add(new SystemChange(SystemChangeKind.Changed, file, was, hash));
        }

        foreach (var (file, was) in before.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!Files.ContainsKey(file)) changes.Add(new SystemChange(SystemChangeKind.Removed, file, was, null));
        }

        return changes;
    }

    /// <summary>Whether the file holds compiled code: a change to it is one the GM is told runs with the server's rights.</summary>
    public static bool IsCompiled(string file) => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}

public enum SystemChangeKind { Version, Added, Removed, Changed }

/// <summary>One difference between two fingerprints. <see cref="File"/> is null for the version; <see cref="Before"/> and
/// <see cref="After"/> are versions for it, and hashes for a file.</summary>
public sealed record SystemChange(SystemChangeKind Kind, string? File, string? Before, string? After);
