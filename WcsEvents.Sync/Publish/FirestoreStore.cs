using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Writes documents to Firestore, skipping any whose content is unchanged since the last write.
/// Documents are built as plain C# values, serialized with the web JSON defaults (camelCase), then
/// turned into Firestore maps, so the frontend reads exactly the shape it would get from JSON.
/// </summary>
public sealed partial class FirestoreStore(FirestoreDb firestore, AppDbContext db, ILogger<FirestoreStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int Written { get; private set; }

    public int Skipped { get; private set; }

    public async Task SetIfChangedAsync(string path, object document, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(document, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        var known = await db.PublishedDocs.FindAsync([path], ct);
        if (known?.Hash == hash)
        {
            Skipped++;
            return;
        }

        using var parsed = JsonDocument.Parse(json);
        var fields = (Dictionary<string, object?>)ToValue(parsed.RootElement)!;
        fields["updatedAt"] = FieldValue.ServerTimestamp;

        await firestore.Document(path).SetAsync(fields, cancellationToken: ct);

        if (known is null)
        {
            db.PublishedDocs.Add(new PublishedDoc { Path = path, Hash = hash, PublishedUtc = DateTime.UtcNow });
        }
        else
        {
            known.Hash = hash;
            known.PublishedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        Written++;
    }

    /// <summary>Every document of a collection the admin edits, by id, as JSON.</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAllAsync(string collection, CancellationToken ct)
    {
        var snapshot = await firestore.Collection(collection).GetSnapshotAsync(ct);
        Dictionary<string, JsonElement> result = [];

        foreach (var doc in snapshot.Documents)
        {
            result[doc.Id] = JsonSerializer.SerializeToElement(FromValue(doc.ToDictionary()), Json);
        }

        LogRead(logger, collection, result.Count);
        return result;
    }

    internal static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static object? FromValue(object? value) => value switch
    {
        IDictionary<string, object> map => map.ToDictionary(p => p.Key, p => FromValue(p.Value)),
        IEnumerable<object> list when value is not string => list.Select(FromValue).ToList(),
        Timestamp t => t.ToDateTime(),
        _ => value,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Read {Count} documents from {Collection}")]
    private static partial void LogRead(ILogger logger, string collection, int count);
}
