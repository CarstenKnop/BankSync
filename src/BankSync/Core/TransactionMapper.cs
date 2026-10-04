using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BankSync.Api;
using BankSync.Data;

namespace BankSync.Core;

/// <summary>Maps Enable Banking transactions to the local entity and computes the deduplication key.</summary>
internal static partial class TransactionMapper
{
    private static readonly JsonSerializerOptions RawJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static TransactionEntity Map(Transaction tx, int accountId, DateTimeOffset now)
    {
        var cdi = tx.CreditDebitIndicator?.ToUpperInvariant();
        var amount = ParseAmount(tx.TransactionAmount?.Value);
        amount = cdi switch
        {
            "DBIT" => -Math.Abs(amount),
            "CRDT" => Math.Abs(amount),
            _ => amount
        };

        var counterparty = cdi == "CRDT" ? tx.Debtor?.Name ?? tx.Creditor?.Name : tx.Creditor?.Name ?? tx.Debtor?.Name;
        var counterpartyAccount = cdi == "CRDT" ? Identification(tx.DebtorAccount) ?? Identification(tx.CreditorAccount)
                                                : Identification(tx.CreditorAccount) ?? Identification(tx.DebtorAccount);

        var text = tx.RemittanceInformation is { Length: > 0 } r
            ? string.Join("\n", r.Where(s => !string.IsNullOrWhiteSpace(s)))
            : tx.Note ?? tx.ReferenceNumber;

        var entity = new TransactionEntity
        {
            AccountId = accountId,
            EntryReference = Clean(tx.EntryReference),
            TransactionId = Clean(tx.TransactionId),
            Status = (tx.Status ?? "BOOK").ToUpperInvariant(),
            BookingDate = ParseDate(tx.BookingDate),
            ValueDate = ParseDate(tx.ValueDate),
            TransactionDate = ParseDate(tx.TransactionDate),
            Amount = amount,
            Currency = tx.TransactionAmount?.Currency ?? "",
            CreditDebit = cdi,
            CounterpartyName = Clean(counterparty),
            CounterpartyAccount = Clean(counterpartyAccount),
            Text = Clean(text),
            TextNormalized = Normalize(text),
            BankTransactionCode = BankCode(tx.BankTransactionCode),
            BalanceAfter = tx.BalanceAfterTransaction?.Value is { } b ? ParseAmount(b) : null,
            MerchantCategoryCode = Clean(tx.MerchantCategoryCode),
            RawJson = JsonSerializer.Serialize(tx, RawJson),
            FirstSeenUtc = now,
            LastSeenUtc = now
        };
        entity.DedupHash = ComputeDedupHash(entity);
        return entity;
    }

    /// <summary>
    /// Preference: entry_reference, then transaction_id, then a content hash. The prefix keeps the three
    /// namespaces apart so a bank that fills a field later cannot collide with an earlier hash.
    /// </summary>
    public static string ComputeDedupHash(TransactionEntity t)
    {
        if (!string.IsNullOrEmpty(t.EntryReference)) return "ER:" + t.EntryReference;
        if (!string.IsNullOrEmpty(t.TransactionId)) return "TID:" + t.TransactionId;
        return "H:" + ContentHash(t);
    }

    public static string ContentHash(TransactionEntity t)
    {
        var material = string.Join("|",
            t.BookingDate?.ToString("yyyy-MM-dd") ?? "",
            t.ValueDate?.ToString("yyyy-MM-dd") ?? "",
            t.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            t.Currency,
            t.CreditDebit ?? "",
            t.TextNormalized ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    public static decimal ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return DateOnly.FromDateTime(dto.UtcDateTime);
        return null;
    }

    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Whitespace().Replace(text, " ").Trim().ToUpperInvariant();
    }

    /// <summary>"PMNT/CCRD" when the bank sends codes; otherwise its description (Nordea sends only "BGS" style descriptions).</summary>
    internal static string? BankCode(BankTransactionCode? c)
    {
        if (c is null) return null;
        var codes = string.Join("/", new[] { c.Code, c.SubCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return Clean(codes.Length > 0 ? codes : c.Description);
    }

    private static string? Identification(AccountIdentification? id)
        => id?.Iban ?? id?.Other?.Identification;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
