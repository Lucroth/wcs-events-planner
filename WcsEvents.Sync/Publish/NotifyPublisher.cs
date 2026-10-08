using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;

namespace WcsEvents.Sync.Publish;

/// <summary>
/// Price alerts. A signed-in reader watches connections of a starred event
/// (<c>users/{uid}/watches/{id}</c>); after the daily fare run, each watch is looked up in the
/// fresh <c>flights</c> and <c>trains</c> documents, and a reader whose watched connections got
/// cheaper since the last run gets one email listing them. Each watch then remembers today's price,
/// so a fare that rises and falls again is reported again.
/// </summary>
public sealed partial class NotifyPublisher(FirestoreDb firestore, FirestoreStore store, TimeProvider time, ILogger<NotifyPublisher> logger)
{
    private const string Site = "https://lucroth.github.io/wcs-events-planner/";

    public async Task PublishAsync(CancellationToken ct)
    {
        var watches = await firestore.CollectionGroup("watches").GetSnapshotAsync(ct);
        if (watches.Count is 0)
        {
            LogDone(logger, 0, 0);
            return;
        }

        var fares = Fares(await store.ReadAllAsync("flights", ct), await store.ReadAllAsync("trains", ct));
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Dictionary<string, List<(DocumentReference Ref, Drop Drop)>> drops = [];
        List<(DocumentReference Ref, decimal Now)> unchanged = [];

        foreach (var doc in watches.Documents)
        {
            var w = Watch.Read(doc.ToDictionary());
            if (w is null || !fares.TryGetValue(w.Key, out var now))
            {
                continue;
            }

            if (now < w.Price - 0.5m)
            {
                var uid = doc.Reference.Parent.Parent!.Id;
                (drops.TryGetValue(uid, out var list) ? list : drops[uid] = []).Add((doc.Reference, new Drop(w, now)));
            }
            else
            {
                unchanged.Add((doc.Reference, now));
            }
        }

        foreach (var (reference, now) in unchanged)
        {
            await reference.UpdateAsync(new Dictionary<string, object> { ["price"] = (double)now, ["checkedOn"] = today }, cancellationToken: ct);
        }

        // A drop is only recorded once its email went out; a failed send keeps the old price, so
        // the next run reports it again.
        var sent = 0;
        foreach (var (uid, list) in drops)
        {
            var user = await firestore.Document($"users/{uid}").GetSnapshotAsync(ct);
            if (!user.TryGetValue<string>("email", out var email) || string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            try
            {
                await SendAsync(email, [.. list.Select(x => x.Drop)], ct);
                sent++;
            }
            catch (Exception ex) when (ex is MailKit.Net.Smtp.SmtpCommandException or MailKit.Net.Smtp.SmtpProtocolException or MailKit.Security.AuthenticationException or IOException)
            {
                LogSendFailed(logger, uid, ex);
                continue;
            }

            foreach (var (reference, drop) in list)
            {
                await reference.UpdateAsync(new Dictionary<string, object>
                {
                    ["price"] = (double)drop.Now,
                    ["previousPrice"] = (double)drop.Watch.Price,
                    ["droppedOn"] = today,
                    ["checkedOn"] = today,
                }, cancellationToken: ct);
            }
        }

        LogDone(logger, drops.Sum(d => d.Value.Count), sent);
    }

    /// <summary>One watched connection, as the event page stores it.</summary>
    internal sealed record Watch(string EventId, string EventName, string Kind, string? Airline, string From, string To, string Date, string? Time, decimal Price, string Currency)
    {
        public string Key => LegKey(EventId, Kind, Airline, From, To, Date, Time);

        public static Watch? Read(IDictionary<string, object> d)
        {
            string? S(string k) => d.TryGetValue(k, out var v) ? v as string : null;
            decimal? N(string k) => d.TryGetValue(k, out var v) ? v switch { double x => (decimal)x, long l => l, _ => null } : null;

            return S("eventId") is { } e && S("kind") is { } kind && S("from") is { } from && S("to") is { } to && S("date") is { } date && N("price") is { } price
                ? new Watch(e, S("eventName") ?? e, kind, S("airline"), from, to, date, S("time"), price, S("currency") ?? "PLN")
                : null;
        }
    }

    internal sealed record Drop(Watch Watch, decimal Now);

    /// <summary>
    /// The same key the event page builds for a leg: a flight by airline, airports and day, a train
    /// by stations, day and departure time.
    /// </summary>
    internal static string LegKey(string eventId, string kind, string? airline, string from, string to, string date, string? time) =>
        kind == "train" ? $"{eventId}|train|{from}|{to}|{date}|{time}" : $"{eventId}|flight|{airline}|{from}|{to}|{date}";

    /// <summary>Today's lowest price per leg across every flights and trains document.</summary>
    internal static Dictionary<string, decimal> Fares(IReadOnlyDictionary<string, JsonElement> flights, IReadOnlyDictionary<string, JsonElement> trains)
    {
        Dictionary<string, decimal> fares = [];

        void Add(string key, decimal price)
        {
            if (!fares.TryGetValue(key, out var known) || price < known)
            {
                fares[key] = price;
            }
        }

        static IEnumerable<JsonElement> List(JsonElement doc, string name) =>
            doc.TryGetProperty(name, out var l) && l.ValueKind is JsonValueKind.Array ? l.EnumerateArray() : [];

        foreach (var doc in flights.Values)
        {
            var eventId = doc.GetProperty("eventId").GetString()!;
            foreach (var leg in List(doc, "out").Concat(List(doc, "back")).Concat(List(doc, "combos").SelectMany(c => new[] { c.GetProperty("out"), c.GetProperty("back") })))
            {
                Add(LegKey(eventId, "flight", leg.GetProperty("airline").GetString(), leg.GetProperty("from").GetString()!, leg.GetProperty("to").GetString()!, leg.GetProperty("date").GetString()!, null), leg.GetProperty("price").GetDecimal());
            }
        }

        foreach (var doc in trains.Values)
        {
            var eventId = doc.GetProperty("eventId").GetString()!;
            var city = doc.GetProperty("city").GetString()!;
            var station = doc.GetProperty("station").GetString()!;
            foreach (var (name, from, to) in new[] { ("out", city, station), ("back", station, city) })
            {
                foreach (var leg in List(doc, name))
                {
                    Add(LegKey(eventId, "train", null, from, to, leg.GetProperty("date").GetString()!, leg.GetProperty("departure").GetString()), leg.GetProperty("price").GetDecimal());
                }
            }
        }

        return fares;
    }

    private static async Task SendAsync(string to, List<Drop> drops, CancellationToken ct)
    {
        var user = Environment.GetEnvironmentVariable("GMAIL_USER") ?? throw new InvalidOperationException("GMAIL_USER is not set.");
        var password = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD") ?? throw new InvalidOperationException("GMAIL_APP_PASSWORD is not set.");

        var message = new MimeKit.MimeMessage
        {
            Subject = drops.Count == 1
                ? $"Cheaper: {Describe(drops[0].Watch)} for {drops[0].Watch.EventName}"
                : $"{drops.Count} connections got cheaper",
            Body = new MimeKit.TextPart(MimeKit.Text.TextFormat.Html) { Text = Body(drops) },
        };
        message.From.Add(new MimeKit.MailboxAddress("WCS Trips", user));
        message.To.Add(MimeKit.MailboxAddress.Parse(to));

        using var smtp = new MailKit.Net.Smtp.SmtpClient();
        await smtp.ConnectAsync("smtp.gmail.com", 465, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        // App passwords are shown in groups of four; Gmail takes them with or without the spaces.
        try
        {
            await smtp.AuthenticateAsync(user.Trim(), password.Replace(" ", "").Trim(), ct);
        }
        catch (Exception ex) when (ex is MailKit.Security.AuthenticationException or MailKit.Net.Smtp.SmtpProtocolException)
        {
            // Never the values: their shape is enough to spot a placeholder, quotes or a short paste.
            throw new MailKit.Security.AuthenticationException(
                $"Gmail refused the login for an address at '{user.Trim().Split('@').LastOrDefault()}' with a {password.Replace(" ", "").Trim().Length}-character password (an app password has 16 letters).", ex);
        }
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }

    internal static string Describe(Watch w) =>
        w.Kind == "train" ? $"train {w.From} → {w.To} {w.Date} {w.Time}" : $"{w.Airline} {w.From} → {w.To} {w.Date}";

    internal static string Body(IEnumerable<Drop> drops)
    {
        var rows = string.Concat(drops.Select(d =>
            $"<li><a href=\"{Site}#/event/{WebUtility.UrlEncode(d.Watch.EventId)}\">{WebUtility.HtmlEncode(d.Watch.EventName)}</a>: " +
            $"{WebUtility.HtmlEncode(Describe(d.Watch))} now <strong>{d.Now:0.##} {d.Watch.Currency}</strong> (was {d.Watch.Price:0.##})</li>"));
        return $"<p>Prices dropped on connections you watch:</p><ul>{rows}</ul>" +
               $"<p style=\"color:#888\">Fares change fast; check before buying. Manage alerts on the event page in <a href=\"{Site}\">WCS Trips</a>.</p>";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Price alert email to user {Uid} failed; the drop is kept for the next run")]
    private static partial void LogSendFailed(ILogger logger, string uid, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Price alerts: {Drops} drops, {Emails} emails sent")]
    private static partial void LogDone(ILogger logger, int drops, int emails);
}
