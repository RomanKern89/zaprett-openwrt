using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Zaprett.Core.Platform;

namespace Zaprett.Service.Platform;

/// <summary>A Network List Manager profile: Id = GUID (registry key name), name, category (0 public, 1 private, 2 domain).</summary>
public sealed record NetworkProfile(string Id, string Name, int Category);

/// <summary>
/// <see cref="INetworkList"/> for <c>--nlm-filter</c> (network_filter.skip_corporate): the GUIDs of every known network
/// that is not domain-authenticated, from HKLM\…\NetworkList\Profiles (winws accepts NLM names and GUIDs; GUIDs have
/// no commas). A network first joined after the engine started is not in the list until the next engine start.
/// </summary>
public sealed partial class NetworkList(Func<IReadOnlyList<NetworkProfile>>? source = null) : INetworkList
{
    public const int DomainCategory = 2;
    private const string ProfilesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    public Task<IReadOnlyList<string>> GetNonCorporateNetworksAsync(CancellationToken ct) =>
        Task.FromResult(Select((source ?? ReadRegistry)()));

    public static IReadOnlyList<string> Select(IEnumerable<NetworkProfile> profiles) =>
        profiles.Where(p => p.Category != DomainCategory && GuidRegex().IsMatch(p.Id))
            .Select(p => p.Id.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<NetworkProfile> ReadRegistry()
    {
        var list = new List<NetworkProfile>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = hklm.OpenSubKey(ProfilesKey);
        if (profiles is null)
            return list;
        foreach (var guid in profiles.GetSubKeyNames())
        {
            using var k = profiles.OpenSubKey(guid);
            if (k?.GetValue("Category") is int category)
                list.Add(new NetworkProfile(guid, k.GetValue("ProfileName") as string ?? "", category));
        }
        return list;
    }
}

/// <summary><see cref="IUpdateControl"/> until the signed update manifest exists (winsetup): not_supported.</summary>
public sealed class UpdateControlStub : IUpdateControl
{
    private static JsonObject NotSupported() => new()
    {
        ["ok"] = false, ["error"] = "not_supported", ["message"] = "Program updates are not available in this build",
    };

    public Task<JsonObject> CheckAsync(string channel, CancellationToken ct) => Task.FromResult(NotSupported());
    public Task<JsonObject> InstallAsync(string channel, CancellationToken ct) => Task.FromResult(NotSupported());
}
