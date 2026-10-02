// Console walkthrough of the Enable Banking flow for one Nordea DK personal account.
//
// Usage:
//   set EB_APPLICATION_ID=<app id>
//   set EB_PRIVATE_KEY_PATH=<path to .pem>
//   set EB_REDIRECT_URL=<redirect url registered in the control panel>
//   dotnet run -- aspsps                      list ASPSPs for DK
//   dotnet run -- auth                        print the consent URL to open on the phone
//   dotnet run -- session <code>              exchange the callback code for a session, print accounts
//   dotnet run -- transactions <uid> <from> <to>   fetch transactions (yyyy-MM-dd), unattended
//
// This is reference code for the developer, not the app. It has no storage, no scheduler and no quota bookkeeping.

using System.Globalization;
using System.Security.Cryptography;
using EnableBanking.Sample;

string Require(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Environment variable {name} is not set");

var appId = Require("EB_APPLICATION_ID");
var keyPath = Require("EB_PRIVATE_KEY_PATH");
var baseUrl = Environment.GetEnvironmentVariable("EB_BASE_URL") ?? EnableBankingClient.DefaultBaseUrl;

using var jwt = JwtFactory.FromFile(appId, keyPath);
using var http = new HttpClient();
var client = new EnableBankingClient(http, jwt, baseUrl);

var command = args.Length > 0 ? args[0] : "help";
try
{
    switch (command)
    {
        case "aspsps":
        {
            var list = await client.GetAspspsAsync(country: "DK", psuType: "personal");
            foreach (var a in list)
                Console.WriteLine($"{a.Name,-30} {a.Country}  consent max {TimeSpan.FromSeconds(a.MaximumConsentValiditySeconds).TotalDays:0} days  beta={a.Beta}  psu headers: {string.Join(",", a.RequiredPsuHeaders ?? [])}");
            break;
        }
        case "auth":
        {
            var redirect = Require("EB_REDIRECT_URL");
            var aspsps = await client.GetAspspsAsync(country: "DK", psuType: "personal");
            var nordea = aspsps.First(a => a.Name == "Nordea");
            var maxDays = TimeSpan.FromSeconds(nordea.MaximumConsentValiditySeconds).TotalDays;
            var validUntil = DateTimeOffset.UtcNow.AddDays(Math.Min(180, maxDays)).AddMinutes(-5);
            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var response = await client.StartAuthorizationAsync(new StartAuthorizationRequest(
                Access: new Access(validUntil),
                Aspsp: new AspspRef("Nordea", "DK"),
                State: state,
                RedirectUrl: redirect,
                PsuType: "personal",
                Language: "da"));

            Console.WriteLine($"state            : {state}   (store it; verify it on the callback)");
            Console.WriteLine($"authorization_id : {response.AuthorizationId}");
            Console.WriteLine($"Open this URL on the phone and approve with MitID:");
            Console.WriteLine(response.Url);
            Console.WriteLine("Then run: dotnet run -- session <code from the callback URL>");
            break;
        }
        case "session":
        {
            var code = args.Length > 1 ? args[1] : throw new ArgumentException("session <code>");
            var session = await client.AuthorizeSessionAsync(code);
            Console.WriteLine($"session_id  : {session.SessionId}   (store encrypted)");
            Console.WriteLine($"valid_until : {session.Access.ValidUntil:u}");
            if (session.Accounts.Length == 0)
                Console.WriteLine("No accounts returned. In restricted production this means the account is not linked in the Enable Banking control panel.");
            foreach (var a in session.Accounts)
                Console.WriteLine($"  uid={a.Uid}  iban={a.AccountId?.Iban}  name={a.Name}  currency={a.Currency}  hash={a.IdentificationHash}");
            break;
        }
        case "transactions":
        {
            if (args.Length < 4) throw new ArgumentException("transactions <uid> <from yyyy-MM-dd> <to yyyy-MM-dd>");
            var uid = args[1];
            var from = DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = DateOnly.ParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var count = 0;
            await foreach (var tx in client.GetTransactionsAsync(uid, from, to))
            {
                count++;
                var text = tx.RemittanceInformation is { Length: > 0 } r ? string.Join(" | ", r) : "";
                var party = tx.Creditor?.Name ?? tx.Debtor?.Name ?? "";
                Console.WriteLine($"{tx.BookingDate} {tx.Status,-4} {tx.CreditDebitIndicator,-4} {tx.TransactionAmount.Value,12} {tx.TransactionAmount.Currency}  {party}  {text}");
            }
            Console.WriteLine($"{count} transactions");
            break;
        }
        default:
            Console.WriteLine("Commands: aspsps | auth | session <code> | transactions <uid> <from> <to>");
            break;
    }
}
catch (EnableBankingException ex)
{
    Console.Error.WriteLine($"Enable Banking error: {ex.Message}");
    if (ex.IsRateLimited) Console.Error.WriteLine("Rate limited: 4 unattended calls per account per day. Wait for the next slot.");
    if (ex.IsSessionExpired) Console.Error.WriteLine("Session expired: start a new authorisation.");
    if (ex.IsAuthProblem) Console.Error.WriteLine("JWT rejected: check application id, key, and that the app is active in this environment.");
    return 1;
}
return 0;
