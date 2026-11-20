using System.Text.Json;

namespace Pos.Shared.Auth;

/// <summary>Permission names checked by the Node Service.</summary>
public static class Permissions
{
    public const string All = "*";
    public const string ShiftOpen = "shift.open";
    public const string ShiftClose = "shift.close";
    public const string SaleCreate = "sale.create";
}

/// <summary>
/// The merged permissions of a user's roles (<c>roles.permissions</c>, a JSON array of strings such as
/// <c>["sale.create","discount.max:10"]</c>). <c>"*"</c> grants everything. Names are lower-case.
/// </summary>
public sealed class PermissionSet
{
    private readonly SortedSet<string> _names;

    private PermissionSet(IEnumerable<string> names) =>
        _names = new SortedSet<string>(
            names.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0),
            StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _names;

    public bool Has(string permission) =>
        _names.Contains(Permissions.All) || _names.Contains(permission.Trim().ToLowerInvariant());

    public static PermissionSet FromNames(IEnumerable<string> names) => new(names);

    /// <exception cref="FormatException">A role's permissions are not a JSON array of strings.</exception>
    public static PermissionSet FromRoleJson(IEnumerable<string?> rolePermissions)
    {
        var names = new List<string>();
        foreach (var json in rolePermissions)
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Null)
                    continue;
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException($"Role permissions must be a JSON array: {json}");

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    names.Add(item.ValueKind == JsonValueKind.String
                        ? item.GetString()!
                        : throw new FormatException($"Role permissions must be strings: {json}"));
                }
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Role permissions are not valid JSON: {json}", ex);
            }
        }

        return new PermissionSet(names);
    }
}
