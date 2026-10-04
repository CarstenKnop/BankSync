using System.IdentityModel.Tokens.Jwt;
using BankSync.Api;
using BankSync.Core;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BankSync.Tests;

public class JwtFactoryTests
{
    [Fact]
    public void Token_has_required_header_and_claims()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        using var factory = new JwtFactory("app-id-1", TestHost.TestPem, TimeSpan.FromMinutes(30), clock);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(factory.GetToken());

        Assert.Equal("RS256", token.Header.Alg);
        Assert.Equal("app-id-1", token.Header.Kid);
        Assert.Equal("enablebanking.com", token.Issuer);
        Assert.Contains("api.enablebanking.com", token.Audiences);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), long.Parse(token.Claims.First(c => c.Type == "iat").Value));
        Assert.Equal(clock.GetUtcNow().AddMinutes(30).UtcDateTime, token.ValidTo);
    }

    [Fact]
    public void Token_is_cached_until_near_expiry()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        using var factory = new JwtFactory("app-id-1", TestHost.TestPem, TimeSpan.FromMinutes(30), clock);
        var first = factory.GetToken();
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(first, factory.GetToken());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.NotEqual(first, factory.GetToken());
    }

    [Fact]
    public void Lifetime_over_24h_is_rejected()
        => Assert.Throws<BankSyncConfigurationException>(() => new JwtFactory("x", TestHost.TestPem, TimeSpan.FromHours(25), TimeProvider.System));
}

public class TransactionMapperTests
{
    private static Transaction Tx(string? entryRef = null, string? txId = null, string cdi = "DBIT", string amount = "249.00", string status = "BOOK", string text = "Dankort-nota  NETTO 1234")
        => new(entryRef, txId, new Amount("DKK", amount), cdi, status, "2026-10-01", "2026-10-01", "2026-09-30",
               new Party("NETTO 1234"), null, null, new AccountIdentification("DK50", null), new BankTransactionCode("PMNT", "CCRD", "Card"),
               [text], new Amount("DKK", "100.00"), "5411", null, null);

    [Fact]
    public void Debit_is_negative_and_credit_positive()
    {
        Assert.Equal(-249.00m, TransactionMapper.Map(Tx(cdi: "DBIT"), 1, DateTimeOffset.UtcNow).Amount);
        Assert.Equal(249.00m, TransactionMapper.Map(Tx(cdi: "CRDT", amount: "-249.00"), 1, DateTimeOffset.UtcNow).Amount);
    }

    [Fact]
    public void Dedup_prefers_entry_reference_then_transaction_id_then_content()
    {
        Assert.Equal("ER:ref-1", TransactionMapper.Map(Tx(entryRef: "ref-1", txId: "tid-1"), 1, DateTimeOffset.UtcNow).DedupHash);
        Assert.Equal("TID:tid-1", TransactionMapper.Map(Tx(txId: "tid-1"), 1, DateTimeOffset.UtcNow).DedupHash);
        var h1 = TransactionMapper.Map(Tx(), 1, DateTimeOffset.UtcNow).DedupHash;
        var h2 = TransactionMapper.Map(Tx(text: "dankort-nota netto 1234"), 1, DateTimeOffset.UtcNow).DedupHash;
        Assert.StartsWith("H:", h1);
        Assert.Equal(h1, h2);   // whitespace and case do not matter
        Assert.NotEqual(h1, TransactionMapper.Map(Tx(amount: "250.00"), 1, DateTimeOffset.UtcNow).DedupHash);
    }

    [Fact]
    public void Counterparty_follows_direction()
    {
        var debit = TransactionMapper.Map(Tx(cdi: "DBIT"), 1, DateTimeOffset.UtcNow);
        Assert.Equal("NETTO 1234", debit.CounterpartyName);
        Assert.Equal("PMNT/CCRD", debit.BankTransactionCode);
        Assert.Equal(100.00m, debit.BalanceAfter);
    }
}

public class SlotCalculatorTests
{
    private static SlotCalculator Calc(params string[] slots) => new(Options.Create(new BankSyncOptions { SyncSlots = slots, TimeZone = "Europe/Copenhagen" }));

    [Fact]
    public void Next_and_last_slot_cross_midnight()
    {
        var calc = Calc("06:30", "11:30", "16:30", "21:30");
        var now = new DateTimeOffset(2026, 10, 2, 22, 0, 0, TimeSpan.FromHours(2));   // 22:00 local, after the last slot

        var next = calc.NextSlot(now)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 6, 30, 0, TimeSpan.FromHours(2)), next);

        var last = calc.LastSlotAtOrBefore(now)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.FromHours(2)), last);

        var early = new DateTimeOffset(2026, 10, 2, 5, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.FromHours(2)), calc.LastSlotAtOrBefore(early)!.Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 6, 30, 0, TimeSpan.FromHours(2)), calc.NextSlot(early)!.Value);
    }

    [Fact]
    public void Local_day_uses_configured_zone()
    {
        var calc = Calc("06:30");
        Assert.Equal("2026-10-03", calc.LocalDay(new DateTimeOffset(2026, 10, 2, 23, 30, 0, TimeSpan.Zero)));   // 01:30 next day in Copenhagen
    }

    [Fact]
    public void Too_many_slots_is_a_configuration_error()
    {
        var o = new BankSyncOptions { ApplicationId = "a", PrivateKeyPem = "x", RedirectUrl = "https://x", SyncSlots = ["01:00", "02:00", "03:00", "04:00", "05:00"] };
        Assert.Throws<BankSyncConfigurationException>(o.Validate);
    }
}

public class OptionsBindingTests
{
    private static BankSyncOptions Bind(Dictionary<string, string?> values)
    {
        var config = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder(), values).Build();
        var o = new BankSyncOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config.GetSection(BankSyncOptions.SectionName), o);
        return o;
    }

    [Fact]
    public void Slots_from_configuration_replace_the_defaults_instead_of_adding_to_them()
    {
        var o = Bind(new()
        {
            ["BankSync:SyncSlots:0"] = "06:30", ["BankSync:SyncSlots:1"] = "11:30",
            ["BankSync:SyncSlots:2"] = "16:30", ["BankSync:SyncSlots:3"] = "21:30",
        });

        Assert.Equal(["06:30", "11:30", "16:30", "21:30"], o.EffectiveSyncSlots);
        o.ApplicationId = "a"; o.PrivateKeyPem = "x"; o.RedirectUrl = "https://x";
        o.Validate();   // must not throw: four configured slots are four slots
    }

    [Fact]
    public void Fewer_configured_slots_are_honoured_and_missing_configuration_uses_defaults()
    {
        Assert.Equal(["08:00", "20:00"], Bind(new() { ["BankSync:SyncSlots:0"] = "08:00", ["BankSync:SyncSlots:1"] = "20:00" }).EffectiveSyncSlots);
        Assert.Equal(["06:30", "11:30", "16:30", "21:30"], Bind(new() { ["BankSync:ApplicationId"] = "a" }).EffectiveSyncSlots);
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void Round_trips_and_persists_key()
    {
        var dir = Path.Combine(Path.GetTempPath(), "banksync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "k.key");
        var a = new SecretProtector(path);
        var protectedText = a.Protect("sess-1");
        Assert.NotEqual("sess-1", protectedText);
        var b = new SecretProtector(path);   // re-reads the same key file
        Assert.Equal("sess-1", b.Unprotect(protectedText));
        Directory.Delete(dir, true);
    }
}
