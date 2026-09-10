using Npgsql;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Reads ezfb form entry ids (uuid) from dynamic SQL result columns.</summary>
public static class EzfbEntryIdReader
{
    public static Guid? ReadOrNull(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid guid => guid == Guid.Empty ? null : guid,
            string text when Guid.TryParse(text, out var parsed) => parsed == Guid.Empty ? null : parsed,
            _ => null
        };
    }
}
